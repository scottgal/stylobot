using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     AI scraper/crawler detection contributor.
///     Detects AI training bots, AI search bots, and AI assistants using multiple signal layers:
///     - Known AI bot User-Agent patterns (GPTBot, ClaudeBot, PerplexityBot, etc.)
///     - Cloudflare "Markdown for Agents" signals (Accept: text/markdown)
///     - Cloudflare AI Gateway headers (cf-aig-*)
///     - Cloudflare Browser Rendering headers (cf-brapi-*, cf-biso-*)
///     - Web Bot Auth cryptographic signatures (RFC 9421)
///     - Content negotiation anomalies for AI consumption
///     - Requests targeting AI discovery endpoints (/llms.txt, /llms-full.txt)
///     - AI scraping service headers (Jina Reader, Firecrawl)
///
///     Configuration loaded from: aiscraper.detector.yaml
///     Override via: appsettings.json -> BotDetection:Detectors:AiScraperContributor:*
/// </summary>
public class AiScraperContributor : ConfiguredContributorBase
{
    /// <summary>
    ///     Known AI bot User-Agent patterns mapped to bot identity.
    ///     Format: { substring -> (BotName, Category, Operator) }
    /// </summary>
    private static readonly List<AiBotPattern> KnownAiBots = new()
    {
        // === AI Training Crawlers ===
        new("GPTBot", "GPTBot", AiBotCategory.Training, "OpenAI"),
        new("ClaudeBot", "ClaudeBot", AiBotCategory.Training, "Anthropic"),
        new("Claude-Web", "Claude-Web", AiBotCategory.Training, "Anthropic"),
        new("anthropic-ai", "Anthropic-AI", AiBotCategory.Training, "Anthropic"),
        new("Google-Extended", "Google-Extended", AiBotCategory.Training, "Google"),
        new("Google-CloudVertexBot", "Google-CloudVertexBot", AiBotCategory.Training, "Google"),
        new("Bytespider", "Bytespider", AiBotCategory.Training, "ByteDance"),
        new("CCBot", "CCBot", AiBotCategory.Training, "Common Crawl"),
        new("Meta-ExternalAgent", "Meta-ExternalAgent", AiBotCategory.Training, "Meta"),
        new("facebookexternalhit", "FacebookBot", AiBotCategory.Training, "Meta"),
        new("Amazonbot", "Amazonbot", AiBotCategory.Training, "Amazon"),
        new("Applebot-Extended", "Applebot-Extended", AiBotCategory.Training, "Apple"),
        new("Diffbot", "Diffbot", AiBotCategory.Training, "Diffbot"),
        new("Cohere-AI", "Cohere-AI", AiBotCategory.Training, "Cohere"),
        new("DeepseekBot", "DeepseekBot", AiBotCategory.Training, "DeepSeek"),
        new("xAI-Bot", "xAI-Bot", AiBotCategory.Training, "xAI"),
        new("AI2Bot", "AI2Bot", AiBotCategory.Training, "Allen Institute"),
        new("HuggingFace-Bot", "HuggingFace-Bot", AiBotCategory.Training, "Hugging Face"),
        new("Brightbot", "Brightbot", AiBotCategory.Training, "Bright Data"),
        new("Together-Bot", "Together-Bot", AiBotCategory.Training, "Together AI"),
        new("ImagesiftBot", "ImagesiftBot", AiBotCategory.Training, "The Hive"),
        new("Webzio-Extended", "Webzio-Extended", AiBotCategory.Training, "Webz.io"),
        new("Kangaroo Bot", "Kangaroo Bot", AiBotCategory.Training, "Unknown"),
        new("PanguBot", "PanguBot", AiBotCategory.Training, "Unknown"),
        new("Timpi", "TimpiBot", AiBotCategory.Training, "Timpi"),

        // === AI Search Bots ===
        new("OAI-SearchBot", "OAI-SearchBot", AiBotCategory.Search, "OpenAI"),
        new("PerplexityBot", "PerplexityBot", AiBotCategory.Search, "Perplexity"),
        new("Perplexity-User", "Perplexity-User", AiBotCategory.Assistant, "Perplexity"),
        new("DuckAssistBot", "DuckAssistBot", AiBotCategory.Search, "DuckDuckGo"),
        new("YouBot", "YouBot", AiBotCategory.Search, "You.com"),
        new("IbouBot", "IbouBot", AiBotCategory.Search, "Ibou.io"),
        new("Andibot", "Andibot", AiBotCategory.Search, "Andi"),

        // === AI Assistants (user-triggered) ===
        new("ChatGPT-User", "ChatGPT-User", AiBotCategory.Assistant, "OpenAI"),
        new("ChatGPT-Browser", "ChatGPT-Browser", AiBotCategory.Assistant, "OpenAI"),
        new("Claude-User", "Claude-User", AiBotCategory.Assistant, "Anthropic"),
        new("Claude-SearchBot", "Claude-SearchBot", AiBotCategory.Search, "Anthropic"),
        new("Meta-ExternalFetcher", "Meta-ExternalFetcher", AiBotCategory.Assistant, "Meta"),
        new("MistralAI-User", "MistralAI-User", AiBotCategory.Assistant, "Mistral"),
        new("Cohere-Command", "Cohere-Command", AiBotCategory.Assistant, "Cohere"),
        new("Google-NotebookLM", "Google-NotebookLM", AiBotCategory.Assistant, "Google"),
        new("Gemini-AI", "Gemini-AI", AiBotCategory.Assistant, "Google"),
        new("Gemini-Deep-Research", "Gemini-Deep-Research", AiBotCategory.Assistant, "Google"),
        new("GoogleAgent-Mariner", "GoogleAgent-Mariner", AiBotCategory.Assistant, "Google"),
        new("Character-AI", "Character-AI", AiBotCategory.Assistant, "Character.AI"),
        new("Devin", "Devin", AiBotCategory.Assistant, "Cognition"),

        // === AI Scraping Services ===
        new("FirecrawlAgent", "FirecrawlAgent", AiBotCategory.ScrapingService, "Firecrawl"),
        new("JinaBot", "JinaBot", AiBotCategory.ScrapingService, "Jina")
    };

    /// <summary>
    ///     AI discovery endpoint paths that only AI systems request.
    /// </summary>
    private static readonly HashSet<string> AiDiscoveryPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/llms.txt",
        "/llms-full.txt",
        "/.well-known/http-message-signatures-directory"
    };

    private readonly ILogger<AiScraperContributor> _logger;

    public AiScraperContributor(
        ILogger<AiScraperContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "AiScraper";
    public override int Priority => Manifest?.Priority ?? 9;

    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters
    private double KnownAiBotConfidence => GetParam("known_ai_bot_confidence", 0.95);
    private double AcceptMarkdownConfidence => GetParam("accept_markdown_confidence", 0.85);
    private double AiGatewayConfidence => GetParam("ai_gateway_confidence", 0.8);
    private double WebBotAuthConfidence => GetParam("web_bot_auth_confidence", 0.95);
    private double AiDiscoveryPathConfidence => GetParam("ai_discovery_path_confidence", 0.7);
    private double BrowserRenderingConfidence => GetParam("browser_rendering_confidence", 0.9);
    private double JinaReaderConfidence => GetParam("jina_reader_confidence", 0.85);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var signals = ImmutableDictionary.CreateBuilder<string, object>();

        try
        {
            var request = state.HttpContext.Request;
            var userAgent = request.Headers.UserAgent.ToString();
            var foundBot = false;

            // 1. Known AI bot User-Agent matching
            if (!string.IsNullOrEmpty(userAgent))
            {
                foreach (var bot in KnownAiBots)
                {
                    if (userAgent.Contains(bot.Pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        signals.Add(SignalKeys.AiScraperDetected, true);
                        signals.Add(SignalKeys.AiScraperName, bot.BotName);
                        signals.Add(SignalKeys.AiScraperOperator, bot.Operator);
                        signals.Add(SignalKeys.AiScraperCategory, bot.Category.ToString());
                        foundBot = true;

                        var botType = bot.Category == AiBotCategory.Training
                            ? BotType.AiBot.ToString()
                            : BotType.GoodBot.ToString();

                        contributions.Add(BotContribution(
                            "AI Scraper",
                            $"Known AI {bot.Category.ToString().ToLowerInvariant()} bot: {bot.BotName} ({bot.Operator})",
                            confidenceOverride: KnownAiBotConfidence,
                            weightMultiplier: 2.0,
                            botType: botType,
                            botName: bot.BotName));

                        break;
                    }
                }
            }

            // 2. Accept: text/markdown — Cloudflare "Markdown for Agents" signal
            var acceptHeader = request.Headers.Accept.ToString();
            if (acceptHeader.Contains("text/markdown", StringComparison.OrdinalIgnoreCase))
            {
                signals.Add("aiscraper.accept_markdown", true);

                if (!foundBot)
                {
                    contributions.Add(BotContribution(
                        "AI Scraper",
                        "Requests text/markdown content (Cloudflare Markdown for Agents signal) — real browsers never send this",
                        confidenceOverride: AcceptMarkdownConfidence,
                        weightMultiplier: 1.8,
                        botType: BotType.AiBot.ToString()));
                }
            }

            // 3. Cloudflare AI Gateway headers (cf-aig-*)
            var hasAigHeaders = false;
            foreach (var header in request.Headers)
            {
                if (header.Key.StartsWith("cf-aig-", StringComparison.OrdinalIgnoreCase))
                {
                    hasAigHeaders = true;
                    signals.Add("aiscraper.cf_aig_header", header.Key);
                    break;
                }
            }

            if (hasAigHeaders)
            {
                signals.Add("aiscraper.cloudflare_ai_gateway", true);

                if (!foundBot)
                {
                    contributions.Add(BotContribution(
                        "AI Scraper",
                        "Cloudflare AI Gateway headers detected (cf-aig-*) — traffic routed through AI infrastructure",
                        confidenceOverride: AiGatewayConfidence,
                        weightMultiplier: 1.6,
                        botType: BotType.AiBot.ToString()));
                }
            }

            // 4. Web Bot Auth — RFC 9421 cryptographic bot verification
            if (request.Headers.ContainsKey("Signature") &&
                request.Headers.ContainsKey("Signature-Input") &&
                request.Headers.ContainsKey("Signature-Agent"))
            {
                var signatureInput = request.Headers["Signature-Input"].ToString();
                var signatureAgent = request.Headers["Signature-Agent"].ToString();
                signals.Add("aiscraper.web_bot_auth", true);
                signals.Add("aiscraper.signature_agent", signatureAgent);

                var isWebBotAuth = signatureInput.Contains("web-bot-auth", StringComparison.OrdinalIgnoreCase);
                signals.Add("aiscraper.web_bot_auth_verified", isWebBotAuth);

                if (isWebBotAuth)
                {
                    contributions.Add(BotContribution(
                        "AI Scraper",
                        $"Cryptographically signed AI bot (Web Bot Auth RFC 9421) from {signatureAgent}",
                        confidenceOverride: WebBotAuthConfidence,
                        weightMultiplier: 2.0,
                        botType: BotType.AiBot.ToString(),
                        botName: ExtractBotNameFromSignatureAgent(signatureAgent)));
                }
            }

            // 5. Cloudflare Browser Rendering headers
            if (request.Headers.ContainsKey("cf-brapi-request-id") ||
                request.Headers.ContainsKey("cf-biso-devtools") ||
                request.Headers.ContainsKey("cf-brapi-devtools"))
            {
                signals.Add("aiscraper.cloudflare_browser_rendering", true);

                if (!foundBot)
                {
                    contributions.Add(BotContribution(
                        "AI Scraper",
                        "Cloudflare Browser Rendering infrastructure detected — AI agent using headless browser",
                        confidenceOverride: BrowserRenderingConfidence,
                        weightMultiplier: 1.8,
                        botType: BotType.AiBot.ToString()));
                }
            }

            // 6. AI discovery endpoint requests
            var path = request.Path.Value ?? "/";
            if (AiDiscoveryPaths.Contains(path))
            {
                signals.Add("aiscraper.ai_discovery_path", path);

                if (!foundBot)
                {
                    contributions.Add(BotContribution(
                        "AI Scraper",
                        $"Request to AI discovery endpoint: {path}",
                        confidenceOverride: AiDiscoveryPathConfidence,
                        weightMultiplier: 1.4,
                        botType: BotType.AiBot.ToString()));
                }
            }

            // 7. Jina Reader API headers
            if (request.Headers.ContainsKey("x-respond-with"))
            {
                var respondWith = request.Headers["x-respond-with"].ToString();
                signals.Add("aiscraper.jina_respond_with", respondWith);

                if (!foundBot)
                {
                    contributions.Add(BotContribution(
                        "AI Scraper",
                        $"Jina Reader API header detected (x-respond-with: {respondWith})",
                        confidenceOverride: JinaReaderConfidence,
                        weightMultiplier: 1.6,
                        botType: BotType.AiBot.ToString(),
                        botName: "Jina Reader"));
                }
            }

            // 8. Content-Signal header (Cloudflare content usage policy)
            if (request.Headers.ContainsKey("Content-Signal"))
            {
                signals.Add("aiscraper.content_signal", request.Headers["Content-Signal"].ToString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing AI scraper signals");
            signals.Add("aiscraper.analysis_error", ex.Message);
        }

        // Ensure last contribution has all signals
        if (contributions.Count == 0)
        {
            contributions.Add(DetectionContribution.Info(
                Name,
                "AI Scraper",
                "No AI scraper signals detected") with { Signals = signals.ToImmutable() });
        }
        else
        {
            var last = contributions[^1];
            contributions[^1] = last with { Signals = signals.ToImmutable() };
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private static string? ExtractBotNameFromSignatureAgent(string signatureAgent)
    {
        // "https://chatgpt.com" -> "ChatGPT"
        var cleaned = signatureAgent.Trim('"', ' ');
        if (cleaned.Contains("chatgpt", StringComparison.OrdinalIgnoreCase)) return "ChatGPT";
        if (cleaned.Contains("cloudflare-browser-rendering", StringComparison.OrdinalIgnoreCase))
            return "Cloudflare Browser Rendering";
        if (cleaned.Contains("anthropic", StringComparison.OrdinalIgnoreCase)) return "Claude";
        if (cleaned.Contains("perplexity", StringComparison.OrdinalIgnoreCase)) return "Perplexity";
        return null;
    }

    private enum AiBotCategory
    {
        Training,
        Search,
        Assistant,
        ScrapingService
    }

    private sealed record AiBotPattern(
        string Pattern,
        string BotName,
        AiBotCategory Category,
        string Operator);
}
