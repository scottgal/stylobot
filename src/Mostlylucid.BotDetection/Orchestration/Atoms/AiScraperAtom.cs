using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that identifies AI training / search /
///     assistant bots via UA patterns loaded from YAML, Cloudflare-emitted
///     AI infrastructure headers, RFC 9421 Web Bot Auth signatures, AI
///     discovery endpoints, and vendor-specific reader APIs.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>AiScraperContributor</c>. Priority 9.
///     </para>
///     <para>
///         All UA / bot lookups go through the YAML-loaded
///         <see cref="BotPatternLoader.AiPatterns"/> and the
///         <see cref="WellKnownBotIndex"/> fallback -- no hardcoded lists
///         (the no-word-lists rule).
///     </para>
///     <para>
///         AiDiscoveryPaths is a small fixed set of protocol identifiers
///         (<c>/llms.txt</c>, <c>/.well-known/http-message-signatures-directory</c>)
///         from published specs, not curated content -- keeps it inline.
///     </para>
/// </remarks>
public sealed class AiScraperAtom : DetectorAtomBase
{
    private static readonly HashSet<string> AiDiscoveryPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/llms.txt",
        "/llms-full.txt",
        "/.well-known/http-message-signatures-directory"
    };

    private readonly IReadOnlyList<BotPatternEntry> _knownAiBots;
    private readonly WellKnownBotIndex _wellKnownBots;
    private readonly ILogger<AiScraperAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AiScraperAtom(
        ILogger<AiScraperAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        BotPatternLoader? patternLoader = null,
        WellKnownBotIndex? wellKnownBots = null)
        : base(name: "AiScraper", category: "AiScraper")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        var loader = patternLoader ?? BotPatternLoader.Default;
        _knownAiBots = loader.AiPatterns.ToList();
        _wellKnownBots = wellKnownBots ?? WellKnownBotIndex.Default;
    }

    public override int Priority => 9;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double KnownAiBotConfidence => _configProvider.GetParameter(Name, "known_ai_bot_confidence", 0.95);
    private double AcceptMarkdownConfidence => _configProvider.GetParameter(Name, "accept_markdown_confidence", 0.85);
    private double AiGatewayConfidence => _configProvider.GetParameter(Name, "ai_gateway_confidence", 0.8);
    private double WebBotAuthConfidence => _configProvider.GetParameter(Name, "web_bot_auth_confidence", 0.95);
    private double AiDiscoveryPathConfidence => _configProvider.GetParameter(Name, "ai_discovery_path_confidence", 0.7);
    private double BrowserRenderingConfidence => _configProvider.GetParameter(Name, "browser_rendering_confidence", 0.9);
    private double JinaReaderConfidence => _configProvider.GetParameter(Name, "jina_reader_confidence", 0.85);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        sink.Raise("aiscraper.ran", sessionId);

        var contributions = new List<DetectionContribution>();
        var request = context.Request;
        var userAgent = request.Headers.UserAgent.ToString();
        var foundBot = false;

        try
        {
            // 1. YAML-loaded AI bot patterns
            if (!string.IsNullOrEmpty(userAgent))
            {
                foreach (var bot in _knownAiBots)
                {
                    if (!userAgent.Contains(bot.Pattern, StringComparison.OrdinalIgnoreCase)) continue;

                    var category = bot.AiCategory ?? "Unknown";
                    sink.Raise($"{SignalKeys.AiScraperDetected}:true", sessionId);
                    sink.Raise($"{SignalKeys.AiScraperName}:{bot.BotName}", sessionId);
                    sink.Raise($"{SignalKeys.AiScraperOperator}:{bot.Vendor}", sessionId);
                    sink.Raise($"{SignalKeys.AiScraperCategory}:{category}", sessionId);
                    foundBot = true;

                    var botType = string.Equals(category, "Training", StringComparison.OrdinalIgnoreCase)
                        ? BotType.AiBot.ToString()
                        : bot.BotType;

                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = KnownAiBotConfidence,
                        Weight = 2.0,
                        Reason = $"Known AI {category.ToLowerInvariant()} bot: {bot.BotName} ({bot.Vendor})",
                        BotType = botType
                    });

                    break;
                }

                // Fallback: well-known-bots catalog
                if (!foundBot && _wellKnownBots.Count > 0)
                {
                    var wkbMatch = _wellKnownBots.TryMatch(userAgent);
                    if (wkbMatch is { BotType: BotType.AiBot })
                    {
                        sink.Raise($"{SignalKeys.AiScraperDetected}:true", sessionId);
                        sink.Raise($"{SignalKeys.AiScraperName}:{wkbMatch.DisplayName}", sessionId);
                        sink.Raise($"{SignalKeys.AiScraperOperator}:", sessionId);
                        sink.Raise($"{SignalKeys.AiScraperCategory}:Unknown", sessionId);
                        foundBot = true;
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = KnownAiBotConfidence,
                            Weight = 2.0,
                            Reason = $"Known AI bot (well-known catalog): {wkbMatch.DisplayName}",
                            BotType = BotType.AiBot.ToString()
                        });
                    }
                }
            }

            // 2. Accept: text/markdown
            var acceptHeader = request.Headers.Accept.ToString();
            if (acceptHeader.Contains("text/markdown", StringComparison.OrdinalIgnoreCase))
            {
                sink.Raise("aiscraper.accept_markdown:true", sessionId);
                if (!foundBot)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = AcceptMarkdownConfidence,
                        Weight = 1.8,
                        Reason = "Requests text/markdown content (Cloudflare Markdown for Agents signal) - real browsers never send this",
                        BotType = BotType.AiBot.ToString()
                    });
                }
            }

            // 3. Cloudflare AI Gateway headers (cf-aig-*)
            var hasAigHeaders = false;
            foreach (var header in request.Headers)
            {
                if (!header.Key.StartsWith("cf-aig-", StringComparison.OrdinalIgnoreCase)) continue;
                hasAigHeaders = true;
                sink.Raise($"aiscraper.cf_aig_header:{header.Key}", sessionId);
                break;
            }

            if (hasAigHeaders)
            {
                sink.Raise("aiscraper.cloudflare_ai_gateway:true", sessionId);
                if (!foundBot)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = AiGatewayConfidence,
                        Weight = 1.6,
                        Reason = "Cloudflare AI Gateway headers detected (cf-aig-*) - traffic routed through AI infrastructure",
                        BotType = BotType.AiBot.ToString()
                    });
                }
            }

            // 4. Web Bot Auth (RFC 9421)
            if (request.Headers.ContainsKey("Signature")
                && request.Headers.ContainsKey("Signature-Input")
                && request.Headers.ContainsKey("Signature-Agent"))
            {
                var signatureInput = request.Headers["Signature-Input"].ToString();
                var signatureAgent = request.Headers["Signature-Agent"].ToString();
                sink.Raise("aiscraper.web_bot_auth:true", sessionId);
                sink.Raise($"aiscraper.signature_agent:{signatureAgent}", sessionId);

                var isWebBotAuth = signatureInput.Contains("web-bot-auth", StringComparison.OrdinalIgnoreCase);
                sink.Raise($"aiscraper.web_bot_auth_verified:{(isWebBotAuth ? "true" : "false")}", sessionId);

                if (isWebBotAuth)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = WebBotAuthConfidence,
                        Weight = 2.0,
                        Reason = $"Cryptographically signed AI bot (Web Bot Auth RFC 9421) from {signatureAgent}",
                        BotType = BotType.AiBot.ToString()
                    });
                }
            }

            // 5. Cloudflare Browser Rendering headers
            if (request.Headers.ContainsKey("cf-brapi-request-id")
                || request.Headers.ContainsKey("cf-biso-devtools")
                || request.Headers.ContainsKey("cf-brapi-devtools"))
            {
                sink.Raise("aiscraper.cloudflare_browser_rendering:true", sessionId);
                if (!foundBot)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = BrowserRenderingConfidence,
                        Weight = 1.8,
                        Reason = "Cloudflare Browser Rendering infrastructure detected - AI agent using headless browser",
                        BotType = BotType.AiBot.ToString()
                    });
                }
            }

            // 6. AI discovery endpoint requests
            var path = request.Path.Value ?? "/";
            if (AiDiscoveryPaths.Contains(path))
            {
                sink.Raise($"aiscraper.ai_discovery_path:{path}", sessionId);
                if (!foundBot)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = AiDiscoveryPathConfidence,
                        Weight = 1.4,
                        Reason = $"Request to AI discovery endpoint: {path}",
                        BotType = BotType.AiBot.ToString()
                    });
                }
            }

            // 7. Jina Reader API
            if (request.Headers.ContainsKey("x-respond-with"))
            {
                var respondWith = request.Headers["x-respond-with"].ToString();
                sink.Raise($"aiscraper.jina_respond_with:{respondWith}", sessionId);
                if (!foundBot)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = JinaReaderConfidence,
                        Weight = 1.6,
                        Reason = $"Jina Reader API header detected (x-respond-with: {respondWith})",
                        BotType = BotType.AiBot.ToString(),
                        BotName = "Jina Reader"
                    });
                }
            }

            // 8. Content-Signal header (Cloudflare content usage policy)
            if (request.Headers.ContainsKey("Content-Signal"))
            {
                sink.Raise($"aiscraper.content_signal:{request.Headers["Content-Signal"]}", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing AI scraper signals");
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "No AI scraper signals detected"));

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }
}
