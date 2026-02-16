using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Security/hacking tool detection contributor for the blackboard orchestrator.
///     Detects penetration testing tools, vulnerability scanners, and exploit frameworks.
///     Runs in first wave (no dependencies) as these tools often reveal themselves immediately in UA.
///     Patterns are fetched from external sources via <see cref="IBotListFetcher" />.
///
///     Configuration loaded from: securitytool.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:SecurityToolContributor:*
/// </summary>
public class SecurityToolContributor : ConfiguredContributorBase
{
    private readonly IBotListFetcher _fetcher;
    private readonly ILogger<SecurityToolContributor> _logger;
    private readonly BotDetectionOptions _options;
    private readonly object _patternLock = new();

    // Cached compiled patterns
    private volatile IReadOnlyList<CompiledSecurityPattern>? _compiledPatterns;
    private DateTime _patternsLastUpdated = DateTime.MinValue;

    public SecurityToolContributor(
        ILogger<SecurityToolContributor> logger,
        IOptions<BotDetectionOptions> options,
        IBotListFetcher fetcher,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _options = options.Value;
        _fetcher = fetcher;
    }

    public override string Name => "SecurityTool";
    public override int Priority => Manifest?.Priority ?? 8;

    // Config-driven parameters from YAML
    private TimeSpan PatternRefreshInterval => TimeSpan.FromSeconds(Config.Timing.CacheRefreshSec);
    private int RegexTimeoutMs => GetParam("regex_timeout_ms", 100);

    // No triggers - runs in first wave with UA analysis
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var userAgent = state.UserAgent;

        if (string.IsNullOrWhiteSpace(userAgent)) return None();

        // Check if security tool detection is enabled (default: true)
        if (!_options.SecurityTools.Enabled) return None();

        // Get or refresh patterns
        var patterns = await GetPatternsAsync(cancellationToken);
        if (patterns.Count == 0) return None();

        // Check all patterns
        foreach (var pattern in patterns)
        {
            bool matched;

            if (pattern.CompiledRegex != null)
                // Regex pattern
                try
                {
                    matched = pattern.CompiledRegex.IsMatch(userAgent);
                }
                catch (RegexMatchTimeoutException)
                {
                    _logger.LogDebug("Regex timeout for pattern: {Pattern}", pattern.Original.Pattern);
                    continue;
                }
            else
                // Simple substring match
                matched = userAgent.Contains(pattern.Original.Pattern, StringComparison.OrdinalIgnoreCase);

            if (matched)
            {
                _logger.LogWarning(
                    "Security tool detected: {ToolName} (Category: {Category}) from IP: {ClientIp}",
                    pattern.Original.Name, pattern.Original.Category, state.ClientIp ?? "unknown");

                return Single(CreateSecurityToolContribution(
                    state,
                    pattern.Original.Name ?? pattern.Original.Pattern,
                    pattern.Original.Category ?? "SecurityTool",
                    0.95,
                    userAgent));
            }
        }

        // No security tool detected - report neutral
        return Single(DetectionContribution.Info(Name, "SecurityTool", "No security tools detected in User-Agent"));
    }

    private async Task<IReadOnlyList<CompiledSecurityPattern>> GetPatternsAsync(CancellationToken cancellationToken)
    {
        // Check if patterns need refresh
        if (_compiledPatterns != null && DateTime.UtcNow - _patternsLastUpdated < PatternRefreshInterval)
            return _compiledPatterns;

        lock (_patternLock)
        {
            // Double-check inside lock
            if (_compiledPatterns != null && DateTime.UtcNow - _patternsLastUpdated < PatternRefreshInterval)
                return _compiledPatterns;
        }

        try
        {
            var sourcePatterns = await _fetcher.GetSecurityToolPatternsAsync(cancellationToken);
            var compiled = CompilePatterns(sourcePatterns);

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

    private static IReadOnlyList<CompiledSecurityPattern> CompilePatterns(List<SecurityToolPattern> patterns)
    {
        var compiled = new List<CompiledSecurityPattern>();

        foreach (var pattern in patterns)
        {
            Regex? regex = null;

            if (pattern.IsRegex)
                try
                {
                    regex = new Regex(
                        pattern.Pattern,
                        RegexOptions.IgnoreCase | RegexOptions.Compiled,
                        TimeSpan.FromMilliseconds(100));
                }
                catch (RegexParseException)
                {
                    // Fall back to substring match
                }

            compiled.Add(new CompiledSecurityPattern(pattern, regex));
        }

        return compiled;
    }

    private DetectionContribution CreateSecurityToolContribution(
        BlackboardState state,
        string toolName,
        string category,
        double confidence,
        string userAgent)
    {
        // Security tools trigger early exit as verified bad bot
        state.WriteSignals([
            new(SignalKeys.SecurityToolDetected, true),
            new(SignalKeys.SecurityToolName, toolName),
            new(SignalKeys.SecurityToolCategory, category),
            new(SignalKeys.UserAgent, userAgent),
            new(SignalKeys.UserAgentIsBot, true),
            new(SignalKeys.UserAgentBotType, BotType.MaliciousBot.ToString()),
            new(SignalKeys.UserAgentBotName, toolName)
        ]);

        return DetectionContribution.VerifiedBot(
                Name,
                toolName,
                $"Security/hacking tool detected: {toolName} (Category: {category})")
            with
            {
                ConfidenceDelta = confidence,
                Weight = 2.0 // High weight - security tools are definitive
            };
    }

    /// <summary>
    ///     Compiled security pattern for efficient matching.
    /// </summary>
    private sealed record CompiledSecurityPattern(SecurityToolPattern Original, Regex? CompiledRegex);
}