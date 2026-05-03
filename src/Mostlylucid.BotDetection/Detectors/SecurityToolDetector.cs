using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Detects security/penetration testing tools based on User-Agent signatures.
///     Identifies common scanners, vulnerability assessment tools, and hacking frameworks.
///     Patterns are fetched from external sources via <see cref="IBotListFetcher" />:
///     - digininja/scanner_user_agents (JSON with metadata)
///     - OWASP CoreRuleSet scanners (text format)
///     Part of the security detection layer for API honeypot integration.
/// </summary>
public class SecurityToolDetector : IDetector
{
    private static readonly TimeSpan PatternRefreshInterval = TimeSpan.FromHours(1);
    private readonly IBotListFetcher _fetcher;
    private readonly ILogger<SecurityToolDetector> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;
    private readonly object _patternLock = new();

    // Cached compiled patterns
    private volatile IReadOnlyList<CompiledSecurityPattern>? _compiledPatterns;
    private DateTime _patternsLastUpdated = DateTime.MinValue;

    public SecurityToolDetector(
        ILogger<SecurityToolDetector> logger,
        IOptions<BotDetectionOptions> options,
        IBotListFetcher fetcher,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _options = options.Value;
        _fetcher = fetcher;
        _metrics = metrics;
    }

    public string Name => "Security Tool Detector";
    public DetectorStage Stage => DetectorStage.RawSignals;

    public async Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new DetectorResult();
        var userAgent = context.Request.Headers.UserAgent.ToString();

        try
        {
            if (string.IsNullOrWhiteSpace(userAgent)) return result;

            // Check if security tool detection is enabled
            if (!_options.SecurityTools.Enabled) return result;

            // Get or refresh patterns
            var patterns = await GetPatternsAsync(cancellationToken);
            if (patterns.Count == 0) return result;

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
                    result.Confidence = 0.95;
                    result.BotType = BotType.MaliciousBot;
                    result.BotName = pattern.Original.Name ?? pattern.Original.Pattern;
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "SecurityTool",
                        Detail =
                            $"Security tool detected: {pattern.Original.Name ?? pattern.Original.Pattern} (Category: {pattern.Original.Category})",
                        ConfidenceImpact = 0.95
                    });

                    _logger.LogWarning(
                        "Security tool detected: {ToolName} (Category: {Category}) from UA: {UserAgent}",
                        pattern.Original.Name, pattern.Original.Category, userAgent);

                    return result;
                }
            }

            return result;
        }
        finally
        {
            stopwatch.Stop();
            _metrics?.RecordDetection(result.Confidence, result.Confidence > 0.5, stopwatch.Elapsed, Name);
        }
    }

    public Task<DetectorResult> DetectAsync(DetectionContext detectionContext)
    {
        return DetectAsync(detectionContext.HttpContext, detectionContext.CancellationToken);
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

    /// <summary>
    ///     Compiled security pattern for efficient matching.
    /// </summary>
    private sealed record CompiledSecurityPattern(SecurityToolPattern Original, Regex? CompiledRegex);
}

/// <summary>
///     Categories of security tools for classification and reporting.
/// </summary>
public enum SecurityToolCategory
{
    /// <summary>SQL injection exploitation tools (SQLMap, Havij)</summary>
    SqlInjection,

    /// <summary>Web application vulnerability scanners (Nikto, Nessus, Acunetix)</summary>
    VulnerabilityScanner,

    /// <summary>Port and network scanners (Nmap, Masscan, ZMap)</summary>
    PortScanner,

    /// <summary>Directory/content brute-force tools (DirBuster, Gobuster, FFUF)</summary>
    DirectoryBruteForce,

    /// <summary>CMS-specific scanners (WPScan, JoomScan)</summary>
    CmsScanner,

    /// <summary>Exploitation frameworks (Metasploit, Commix)</summary>
    ExploitFramework,

    /// <summary>Credential brute-force tools (Hydra, Medusa)</summary>
    CredentialAttack,

    /// <summary>Web application proxy/interceptors used for scanning</summary>
    WebProxy,

    /// <summary>Reconnaissance and fingerprinting tools (WhatWeb, Shodan)</summary>
    Reconnaissance,

    /// <summary>Suspicious patterns (misspellings, known malware)</summary>
    Suspicious,

    /// <summary>Other security-related tools</summary>
    Other
}