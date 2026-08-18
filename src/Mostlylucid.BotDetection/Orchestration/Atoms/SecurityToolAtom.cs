using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that detects penetration-testing tools,
///     vulnerability scanners, and exploit frameworks in the User-Agent
///     string. Runs in the foundation wave (Priority 8) alongside UA
///     analysis -- security tools usually reveal themselves immediately.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>SecurityToolContributor</c>. Patterns come from
///         <see cref="IBotListFetcher"/> and are refreshed on the interval
///         defined by the shared timing config; regexes and substring
///         matchers cache in-process.
///     </para>
///     <para>
///         Legacy contributor wrote <see cref="SignalKeys.UserAgent"/> (the
///         raw UA string) to the sink on a match. This atom does NOT emit
///         raw UA per the state-vs-signal PII rule -- the tool NAME
///         (sqlmap, nikto, etc.) and CATEGORY are public labels that stay
///         as Model-2 hints; raw UA is available via HttpContext to any
///         atom that legitimately needs it.
///     </para>
/// </remarks>
public sealed class SecurityToolAtom : DetectorAtomBase
{
    private readonly IBotListFetcher _fetcher;
    private readonly ILogger<SecurityToolAtom> _logger;
    private readonly BotDetectionOptions _options;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly object _patternLock = new();

    private volatile IReadOnlyList<CompiledSecurityPattern>? _compiledPatterns;
    private DateTime _patternsLastUpdated = DateTime.MinValue;

    public SecurityToolAtom(
        ILogger<SecurityToolAtom> logger,
        IOptions<BotDetectionOptions> options,
        IBotListFetcher fetcher,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "SecurityTool", category: "SecurityTool")
    {
        _logger = logger;
        _options = options.Value;
        _fetcher = fetcher;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 8;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private TimeSpan PatternRefreshInterval
        => TimeSpan.FromSeconds(_configProvider.GetDefaults(Name).Timing.CacheRefreshSec);
    private int RegexTimeoutMs => _configProvider.GetParameter(Name, "regex_timeout_ms", 100);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent)) return None();
        if (!_options.SecurityTools.Enabled) return None();

        var patterns = await GetPatternsAsync(ct).ConfigureAwait(false);
        if (patterns.Count == 0) return None();

        foreach (var pattern in patterns)
        {
            bool matched;
            if (pattern.CompiledRegex is not null)
            {
                try
                {
                    matched = pattern.CompiledRegex.IsMatch(userAgent);
                }
                catch (RegexMatchTimeoutException)
                {
                    _logger.LogDebug("Regex timeout for pattern: {Pattern}", pattern.Original.Pattern);
                    continue;
                }
            }
            else
            {
                matched = userAgent.Contains(pattern.Original.Pattern, StringComparison.OrdinalIgnoreCase);
            }

            if (!matched) continue;

            var toolName = pattern.Original.Name ?? pattern.Original.Pattern;
            var category = pattern.Original.Category ?? "SecurityTool";

            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _logger.LogWarning(
                "Security tool detected: {ToolName} (Category: {Category}) from IP: {ClientIp}",
                toolName, category, clientIp);

            // Public labels only -- tool name + category are catalog values, not PII.
            // Colon-encoded: a bare raise here left CveFingerprintAtom's SecurityTool radar
            // dimension permanently 0.0 regardless of a real match (2026-08-17 finding).
            sink.Raise($"{SignalKeys.SecurityToolDetected}:true", sessionId);
            sink.Raise($"{SignalKeys.SecurityToolName}:{toolName}", sessionId);
            sink.Raise($"{SignalKeys.SecurityToolCategory}:{category}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentIsBot}:true", sessionId);
            sink.Raise($"{SignalKeys.UserAgentBotType}:{BotType.MaliciousBot}", sessionId);
            sink.Raise($"{SignalKeys.UserAgentBotName}:{toolName}", sessionId);

            return Single(DetectionContribution.VerifiedBot(
                    Name, toolName,
                    $"Security/hacking tool detected: {toolName} (Category: {category})")
                with
                {
                    ConfidenceDelta = 0.95,
                    Weight = 2.0
                });
        }

        return Single(DetectionContribution.Info(Name, Category, "No security tools detected in User-Agent"));
    }

    private async Task<IReadOnlyList<CompiledSecurityPattern>> GetPatternsAsync(CancellationToken ct)
    {
        if (_compiledPatterns is not null && DateTime.UtcNow - _patternsLastUpdated < PatternRefreshInterval)
            return _compiledPatterns;

        lock (_patternLock)
        {
            if (_compiledPatterns is not null && DateTime.UtcNow - _patternsLastUpdated < PatternRefreshInterval)
                return _compiledPatterns;
        }

        try
        {
            var sourcePatterns = await _fetcher.GetSecurityToolPatternsAsync(ct).ConfigureAwait(false);
            var compiled = CompilePatterns(sourcePatterns, RegexTimeoutMs);

            lock (_patternLock)
            {
                _compiledPatterns = compiled;
                _patternsLastUpdated = DateTime.UtcNow;
            }

            _logger.LogDebug("Loaded {Count} security tool patterns", compiled.Count);
            return compiled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch security tool patterns, using cached");
            return _compiledPatterns ?? Array.Empty<CompiledSecurityPattern>();
        }
    }

    private static IReadOnlyList<CompiledSecurityPattern> CompilePatterns(
        List<SecurityToolPattern> patterns,
        int regexTimeoutMs)
    {
        var compiled = new List<CompiledSecurityPattern>();
        foreach (var pattern in patterns)
        {
            Regex? regex = null;
            if (pattern.IsRegex)
            {
                try
                {
                    regex = new Regex(
                        pattern.Pattern,
                        RegexOptions.IgnoreCase | RegexOptions.Compiled,
                        TimeSpan.FromMilliseconds(regexTimeoutMs));
                }
                catch (RegexParseException)
                {
                    // Fall back to substring match
                }
            }
            compiled.Add(new CompiledSecurityPattern(pattern, regex));
        }
        return compiled;
    }

    private sealed record CompiledSecurityPattern(SecurityToolPattern Original, Regex? CompiledRegex);
}
