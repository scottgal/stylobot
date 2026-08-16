using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     SensorAtom (per Taxonomy.md) that classifies User-Agent strings via
///     YAML-loaded bot patterns, the compiled pattern cache, and the
///     Arcjet well-known-bots catalog. Priority 10, Wave 0. Emits the
///     canonical UA-family / bot-name / bot-type / bot-instance hints
///     every deferred detector keys off.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>UserAgentContributor</c>. Preserves the fediverse-instance
///         discriminator composition, the whitelist / bot-pattern / suspicious
///         evaluation ladder, and the tool-header verification carve-out.
///     </para>
///     <para>
///         State-vs-signal note: the legacy contributor wrote
///         <see cref="SignalKeys.UserAgent"/> (raw UA string) to the sink.
///         This atom keeps that write for parity because <c>SignatureRiskVerdictComposer</c>
///         and multiple downstream atoms key off it -- migrating downstream
///         to <c>HttpContext.Request.Headers.UserAgent</c> reads is a
///         follow-on. All other emissions are protocol-family labels /
///         bot names / booleans / short display strings, no PII.
///     </para>
/// </remarks>
public sealed partial class UserAgentAtom : DetectorAtomBase
{
    private readonly ILogger<UserAgentAtom> _logger;
    private readonly BotDetectionOptions _options;
    private readonly ICompiledPatternCache? _patternCache;
    private readonly WellKnownBotIndex? _wellKnownBots;
    private readonly (string Pattern, BotType Type, string Name)[] _botPatterns;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public UserAgentAtom(
        ILogger<UserAgentAtom> logger,
        IOptions<BotDetectionOptions> options,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        WellKnownBotIndex? wellKnownBots = null,
        BotPatternLoader? patternLoader = null,
        ICompiledPatternCache? patternCache = null)
        : base(name: "UserAgent", category: "UserAgent")
    {
        _logger = logger;
        _options = options.Value;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _patternCache = patternCache;
        _wellKnownBots = wellKnownBots;

        var loader = patternLoader ?? BotPatternLoader.Default;
        _botPatterns = loader.AllPatterns
            .Where(p => Enum.TryParse<BotType>(p.BotType, true, out _))
            .Select(p => (p.Pattern, Enum.Parse<BotType>(p.BotType, true), p.BotName))
            .ToArray();
    }

    public override int Priority => 10;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private int MinUaLength => _configProvider.GetParameter(Name, "min_ua_length", 10);
    private double MissingUaConfidence => _configProvider.GetParameter(Name, "missing_ua_confidence", 0.8);
    private double PatternMatchConfidence => _configProvider.GetParameter(Name, "pattern_match_confidence", 0.9);
    private double SuspiciousConfidence => _configProvider.GetParameter(Name, "suspicious_confidence", 0.6);
    private double ToolHeaderMatchConfidence => _configProvider.GetParameter(Name, "tool_header_match_confidence", 0.7);
    private double ToolHeaderMismatchConfidence => _configProvider.GetParameter(Name, "tool_header_mismatch_confidence", 0.5);

    // A UA that merely "looks like a browser" is the single most-spoofed surface in the system, so
    // the UA atom -- a contributor, NOT the arbiter of "human" -- may only lean WEAKLY toward human
    // on it. It must never emit a confident human verdict a browser-costumed bot can hide behind.
    // Bot markers still emit the strong PatternMatchConfidence signal; this is only the
    // no-bot-marker fallback. Small negative (default -0.05, vs the old arbiter-strength -0.15);
    // calibratable via the manifest.
    private double BrowserUaHumanLean => _configProvider.GetParameter(Name, "browser_ua_human_lean", -0.05);
    private double WeightBotSignal => _configProvider.GetDefaults(Name).Weights.BotSignal;

    private static readonly string[] BrowserOnlyHeaders =
        ["Sec-Fetch-Mode", "Sec-Fetch-Site", "Sec-Fetch-Dest"];

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var userAgent = context.Request.Headers.UserAgent.ToString();

        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return Task.FromResult(Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = MissingUaConfidence,
                Weight = 1.0,
                Reason = "Missing User-Agent header",
                BotType = BotType.Unknown.ToString()
            }));
        }

        var contributions = new List<DetectionContribution>();
        var botInstance = UserAgentDiscriminator.ExtractDiscriminator(userAgent);
        var (family, familyVersion) = UserAgentParser.Parse(userAgent);

        var (isWhitelisted, whitelistName) = CheckWhitelist(userAgent);
        if (isWhitelisted)
        {
            var whitelistDisplay = !string.IsNullOrEmpty(botInstance)
                ? $"{whitelistName} {botInstance}"
                : whitelistName;

            sink.Raise($"{SignalKeys.UserAgent}:{userAgent}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentIsBot}:true", sessionId);
            sink.Raise($"{SignalKeys.UserAgentBotType}:{BotType.SearchEngine}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentBotName}:{whitelistDisplay}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentFamily}:{whitelistDisplay}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentFamilyVersion}:{familyVersion ?? string.Empty}", sessionId);
            if (!string.IsNullOrEmpty(botInstance))
                sink.Raise($"{SignalKeys.UserAgentBotInstance}:{botInstance}", sessionId);

            return Task.FromResult(Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = PatternMatchConfidence,
                Weight = WeightBotSignal,
                Reason = $"Known bot UA pattern: {whitelistDisplay}",
                BotType = BotType.SearchEngine.ToString()
            }));
        }

        var (isBot, confidence, botType, botName, reason) = AnalyzeUserAgent(userAgent);

        if (isBot)
        {
            if (botType == BotType.Tool)
                confidence = VerifyToolHeaders(context, confidence, botName, out reason);

            var displayBotName = !string.IsNullOrEmpty(botInstance) && !string.IsNullOrEmpty(botName)
                ? $"{botName} {botInstance}"
                : botName;

            sink.Raise($"{SignalKeys.UserAgent}:{userAgent}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentIsBot}:true", sessionId);
            sink.Raise($"{SignalKeys.UserAgentBotType}:{botType?.ToString() ?? "Unknown"}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentBotName}:{displayBotName ?? string.Empty}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentFamily}:{displayBotName ?? family}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentFamilyVersion}:{familyVersion ?? string.Empty}", sessionId);
            if (!string.IsNullOrEmpty(botInstance))
                sink.Raise($"{SignalKeys.UserAgentBotInstance}:{botInstance}", sessionId);

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = confidence,
                Weight = WeightBotSignal,
                Reason = reason,
                BotType = botType?.ToString()
            });
        }
        else
        {
            sink.Raise($"{SignalKeys.UserAgent}:{userAgent}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentIsBot}:false", sessionId);
            sink.Raise($"{SignalKeys.UserAgentFamily}:{family}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentFamilyVersion}:{familyVersion ?? string.Empty}", sessionId);
            if (!string.IsNullOrEmpty(botInstance))
                sink.Raise($"{SignalKeys.UserAgentBotInstance}:{botInstance}", sessionId);

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                // Weak human lean only -- a plausible-browser UA is the most-spoofed surface, so it
                // cannot be a confident human verdict. The aggregate decides human/bot across all
                // contributors; this atom only observes "no bot marker in the UA".
                ConfidenceDelta = BrowserUaHumanLean,
                Weight = 1.0,
                Reason = "No bot marker in User-Agent (weak human lean; UA is easily spoofed)"
            });
        }

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private (bool isWhitelisted, string? name) CheckWhitelist(string userAgent)
    {
        var knownBotPatterns = GetStringListParam("known_bot_patterns");
        foreach (var pattern in knownBotPatterns)
            if (userAgent.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return (true, pattern);

        foreach (var pattern in _options.WhitelistedBotPatterns)
            if (userAgent.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return (true, pattern);

        return (false, null);
    }

    private (bool isBot, double confidence, BotType? type, string? name, string reason)
        AnalyzeUserAgent(string userAgent)
    {
        if (IsCommonBotPattern(userAgent, out var botType, out var botName))
            return (true, PatternMatchConfidence, botType, botName, $"Known bot pattern: {botName}");

        if (_patternCache is not null && _patternCache.MatchesAnyPattern(userAgent, out var matchedValue))
        {
            var extractedName = ExtractNameFromUserAgent(userAgent);
            return (true, PatternMatchConfidence, BotType.Unknown, extractedName,
                extractedName is not null
                    ? $"Known bot pattern: {extractedName}"
                    : DescribeMatchedUserAgent(userAgent, matchedValue));
        }

        if (_wellKnownBots is { Count: > 0 })
        {
            var match = _wellKnownBots.TryMatch(userAgent);
            if (match is not null)
                // Family, NOT DisplayName: the family is the accepted literal the UA
                // carried (PetalBot), the DisplayName is the title-cased catalog id
                // (Petalsearch Crawler). Writing the description here surfaced raw
                // crawler names on Top Bots (operator P0, 2026-08-12).
                return (true, PatternMatchConfidence, match.BotType, match.Family,
                    $"Well-known bot: {match.Id}");
        }

        if (IsSuspiciousUserAgent(userAgent, out var suspiciousReason))
        {
            var extractedName2 = ExtractNameFromUserAgent(userAgent);
            return (true, SuspiciousConfidence, BotType.Unknown, extractedName2, suspiciousReason);
        }

        return (false, 0.0, null, null, "Normal user agent");
    }

    private bool IsCommonBotPattern(string userAgent, out BotType? botType, out string? botName)
    {
        foreach (var (pattern, type, name) in _botPatterns)
        {
            if (userAgent.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                botType = type;
                botName = name;
                return true;
            }
        }
        botType = null;
        botName = null;
        return false;
    }

    private bool IsSuspiciousUserAgent(string userAgent, out string reason)
    {
        if (userAgent.Length < MinUaLength)
        {
            reason = $"Suspiciously short User-Agent (< {MinUaLength} chars)";
            return true;
        }
        if (BotKeywordRegex().IsMatch(userAgent))
        {
            reason = "Contains bot/crawler keyword";
            return true;
        }
        if (BareMozillaRegex().IsMatch(userAgent))
        {
            reason = "Bare Mozilla version without details";
            return true;
        }
        // The "(+URL)" contact annotation is the well-known polite-crawler self-identification
        // convention (Googlebot/Bingbot use it inside a "compatible; ..." token, which the
        // BotKeywordRegex/whitelist paths already catch; this branch is for tools that self-
        // declare with the SAME convention but no "bot/crawler/spider/scraper" keyword -- e.g.
        // "Software Security Research/1.0 (+https://...)"). No real browser UA embeds a "+"-
        // prefixed URL in a parenthetical, so this is a generic, low-false-positive class
        // signal, not a per-fingerprint or per-name rule.
        if (SelfDeclaredContactUrlRegex().IsMatch(userAgent))
        {
            reason = "Self-declaring contact URL in User-Agent (polite-crawler convention)";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    private static string? ExtractNameFromUserAgent(string userAgent)
    {
        var compatibleMatch = CompatibleBotRegex().Match(userAgent);
        if (compatibleMatch.Success) return compatibleMatch.Groups[1].Value;

        if (!userAgent.Contains("Mozilla/") || userAgent.Length < 60)
        {
            var simpleMatch = SimpleToolRegex().Match(userAgent);
            if (simpleMatch.Success)
            {
                var name = simpleMatch.Groups[1].Value;
                if (!string.Equals(name, "Mozilla", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "HTTP", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }
        return null;
    }

    private static string DescribeMatchedUserAgent(string userAgent, string? matchedValue)
    {
        if (SimpleToolRegex().IsMatch(userAgent) && !userAgent.Contains(' '))
            return $"Simple tool-style User-Agent: {Truncate(userAgent, 60)} (no browser information)";
        if (BotKeywordRegex().IsMatch(userAgent))
            return $"User-Agent contains bot identifier: {Truncate(userAgent, 60)}";
        if (userAgent.Length < 30)
            return $"Minimal User-Agent: {Truncate(userAgent, 60)} (too short for a real browser)";
        if (!string.IsNullOrEmpty(matchedValue) && matchedValue.Length > 2)
            return $"User-Agent matches known bot signature: {Truncate(userAgent, 60)}";
        return $"User-Agent matches known bot pattern database: {Truncate(userAgent, 60)}";
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";

    private double VerifyToolHeaders(HttpContext context, double baseConfidence, string? toolName, out string reason)
    {
        var headers = context.Request.Headers;
        var browserHeaderCount = 0;
        foreach (var header in BrowserOnlyHeaders)
            if (headers.ContainsKey(header))
                browserHeaderCount++;

        var acceptLang = headers["Accept-Language"].FirstOrDefault();
        if (!string.IsNullOrEmpty(acceptLang) && acceptLang.Contains(','))
            browserHeaderCount++;

        if (browserHeaderCount > 0)
        {
            reason = $"Tool UA ({toolName}) with {browserHeaderCount} browser-only header(s) - likely spoofed";
            return ToolHeaderMismatchConfidence + (browserHeaderCount * 0.05);
        }

        reason = $"Confirmed tool: {toolName} (headers match expected fingerprint)";
        return ToolHeaderMatchConfidence;
    }

    private IReadOnlyList<string> GetStringListParam(string paramName)
    {
        var parameters = _configProvider.GetDefaults(Name).Parameters;
        if (parameters.TryGetValue(paramName, out var value))
        {
            if (value is IEnumerable<string> strings) return strings.ToArray();
            if (value is IEnumerable<object> enumerable)
                return enumerable.Select(x => x?.ToString() ?? string.Empty).ToArray();
        }
        return Array.Empty<string>();
    }

    [GeneratedRegex(@"\b(bot|crawler|spider|scraper)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BotKeywordRegex();

    [GeneratedRegex(@"Mozilla/\d+\.\d+\s*$")]
    private static partial Regex BareMozillaRegex();

    [GeneratedRegex(@"compatible;\s*([A-Za-z][A-Za-z0-9_-]+)")]
    private static partial Regex CompatibleBotRegex();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_.-]+)/")]
    private static partial Regex SimpleToolRegex();

    [GeneratedRegex(@"\(\+https?://", RegexOptions.IgnoreCase)]
    private static partial Regex SelfDeclaredContactUrlRegex();
}
