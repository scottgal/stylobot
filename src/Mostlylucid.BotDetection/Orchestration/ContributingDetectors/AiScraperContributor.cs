using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
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
    // Loaded from ai-scrapers.bot-patterns.yaml (and any other YAML with ai_category set).
    // To add or modify AI bot patterns, edit the YAML files — never add hardcoded entries here.
    private readonly IReadOnlyList<BotPatternEntry> _knownAiBots;

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
        IDetectorConfigProvider configProvider,
        BotPatternLoader? patternLoader = null)
        : base(configProvider)
    {
        _logger = logger;
        var loader = patternLoader ?? BotPatternLoader.Default;
        _knownAiBots = loader.AiPatterns.ToList();
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

        try
        {
            var request = state.HttpContext.Request;
            var userAgent = request.Headers.UserAgent.ToString();
            var foundBot = false;

            // 1. Known AI bot User-Agent matching (loaded from YAML)
            if (!string.IsNullOrEmpty(userAgent))
            {
                foreach (var bot in _knownAiBots)
                {
                    if (userAgent.Contains(bot.Pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        var category = bot.AiCategory ?? "Unknown";
                        state.WriteSignals([
                            new(SignalKeys.AiScraperDetected, true),
                            new(SignalKeys.AiScraperName, bot.BotName),
                            new(SignalKeys.AiScraperOperator, bot.Vendor),
                            new(SignalKeys.AiScraperCategory, category)
                        ]);
                        foundBot = true;

                        // Training bots → AiBot (harvesting content); Search/Assistant/ScrapingService → use YAML bot_type
                        var botType = string.Equals(category, "Training", StringComparison.OrdinalIgnoreCase)
                            ? BotType.AiBot.ToString()
                            : bot.BotType;

                        contributions.Add(BotContribution(
                            "AI Scraper",
                            $"Known AI {category.ToLowerInvariant()} bot: {bot.BotName} ({bot.Vendor})",
                            confidenceOverride: KnownAiBotConfidence,
                            weightMultiplier: 2.0,
                            botType: botType,
                            botName: bot.BotName));

                        break;
                    }
                }
            }

            // 2. Accept: text/markdown - Cloudflare "Markdown for Agents" signal
            var acceptHeader = request.Headers.Accept.ToString();
            if (acceptHeader.Contains("text/markdown", StringComparison.OrdinalIgnoreCase))
            {
                state.WriteSignal("aiscraper.accept_markdown", true);

                if (!foundBot)
                {
                    contributions.Add(BotContribution(
                        "AI Scraper",
                        "Requests text/markdown content (Cloudflare Markdown for Agents signal) - real browsers never send this",
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
                    state.WriteSignal("aiscraper.cf_aig_header", header.Key);
                    break;
                }
            }

            if (hasAigHeaders)
            {
                state.WriteSignal("aiscraper.cloudflare_ai_gateway", true);

                if (!foundBot)
                {
                    contributions.Add(BotContribution(
                        "AI Scraper",
                        "Cloudflare AI Gateway headers detected (cf-aig-*) - traffic routed through AI infrastructure",
                        confidenceOverride: AiGatewayConfidence,
                        weightMultiplier: 1.6,
                        botType: BotType.AiBot.ToString()));
                }
            }

            // 4. Web Bot Auth - RFC 9421 cryptographic bot verification
            if (request.Headers.ContainsKey("Signature") &&
                request.Headers.ContainsKey("Signature-Input") &&
                request.Headers.ContainsKey("Signature-Agent"))
            {
                var signatureInput = request.Headers["Signature-Input"].ToString();
                var signatureAgent = request.Headers["Signature-Agent"].ToString();
                state.WriteSignal("aiscraper.web_bot_auth", true);
                state.WriteSignal("aiscraper.signature_agent", signatureAgent);

                var isWebBotAuth = signatureInput.Contains("web-bot-auth", StringComparison.OrdinalIgnoreCase);
                state.WriteSignal("aiscraper.web_bot_auth_verified", isWebBotAuth);

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
                state.WriteSignal("aiscraper.cloudflare_browser_rendering", true);

                if (!foundBot)
                {
                    contributions.Add(BotContribution(
                        "AI Scraper",
                        "Cloudflare Browser Rendering infrastructure detected - AI agent using headless browser",
                        confidenceOverride: BrowserRenderingConfidence,
                        weightMultiplier: 1.8,
                        botType: BotType.AiBot.ToString()));
                }
            }

            // 6. AI discovery endpoint requests
            var path = request.Path.Value ?? "/";
            if (AiDiscoveryPaths.Contains(path))
            {
                state.WriteSignal("aiscraper.ai_discovery_path", path);

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
                state.WriteSignal("aiscraper.jina_respond_with", respondWith);

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
                state.WriteSignal("aiscraper.content_signal", request.Headers["Content-Signal"].ToString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing AI scraper signals");
            state.WriteSignal("aiscraper.analysis_error", ex.Message);
        }

        // If no contributions, add neutral
        if (contributions.Count == 0)
        {
            contributions.Add(DetectionContribution.Info(
                Name,
                "AI Scraper",
                "No AI scraper signals detected"));
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

}