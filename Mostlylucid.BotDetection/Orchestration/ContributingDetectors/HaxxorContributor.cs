using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Detects script kiddie probing, injection attempts, config file exposure scans,
///     webshell probes, and attack tool signatures from request metadata.
///     Pure single-request analysis — no cross-request state needed.
///
///     Categories detected:
///     - SQL injection (UNION SELECT, OR 1=1, SLEEP, WAITFOR DELAY)
///     - XSS (script tags, event handlers, javascript: URIs)
///     - Path traversal (../../etc/passwd, encoded variants)
///     - Command injection (shell commands in query params)
///     - SSRF (localhost, metadata endpoints in params)
///     - Template injection (Jinja2, EL, Freemarker patterns)
///     - Path probing (wp-admin, .env, .git, phpmyadmin)
///     - Config exposure (.env, appsettings.json, docker-compose.yml)
///     - Webshell probing (c99.php, r57.php, shell.php)
///     - Backup/dump scanning (.sql, .bak, .old, .swp)
///     - Admin panel scanning (/admin, /cpanel, /grafana, /jenkins)
///     - Debug endpoint exposure (/actuator, /server-status, /elmah.axd)
///     - Encoding evasion (double-encoding, null bytes, overlong UTF-8)
///
///     Works alongside SecurityToolContributor (which detects tool UAs) and
///     ResponseBehaviorContributor (which tracks 404 scan patterns).
///     HaxxorContributor identifies the *attack payloads* regardless of UA.
///
///     Configuration loaded from: haxxor.detector.yaml
///     Override via: appsettings.json -> BotDetection:Detectors:HaxxorContributor:*
/// </summary>
public class HaxxorContributor : ConfiguredContributorBase
{
    private readonly ILogger<HaxxorContributor> _logger;

    // Lazily compiled regex patterns per category — built once, thread-safe via volatile
    private volatile CompiledPatternSet? _compiledPatterns;

    // Pre-lowered and frozen path pattern sets — built once alongside regex compilation
    private volatile CachedPathPatterns? _cachedPathPatterns;

    // Static empty result to avoid allocation on clean path (vast majority of requests)
    private static readonly Task<IReadOnlyList<DetectionContribution>> EmptyResult =
        Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

    public HaxxorContributor(
        ILogger<HaxxorContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "Haxxor";
    public override int Priority => Manifest?.Priority ?? 7;

    // Wave 0 — no trigger conditions, runs on every request
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters from YAML — cached after first access via GetParam
    private int MaxInputLength => GetParam("max_input_length", 8192);
    private int RegexTimeoutMs => GetParam("regex_timeout_ms", 100);
    private double CompoundBonusPerCategory => GetParam("compound_bonus_per_category", 0.05);
    private double MaxCompoundConfidence => GetParam("max_compound_confidence", 0.99);

    /// <summary>
    ///     Categories that represent active injection attacks (vs passive scanning).
    ///     FrozenSet for O(1) lookups with zero per-lookup allocation.
    /// </summary>
    private static readonly FrozenSet<string> InjectionCategories =
        new[] { "sqli", "xss", "traversal", "cmdi", "ssrf", "ssti" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Map category name to signal key — frozen for fast lookup.
    /// </summary>
    private static readonly FrozenDictionary<string, string> CategorySignalKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sqli"] = SignalKeys.AttackSqli,
            ["xss"] = SignalKeys.AttackXss,
            ["traversal"] = SignalKeys.AttackTraversal,
            ["cmdi"] = SignalKeys.AttackCmdi,
            ["ssrf"] = SignalKeys.AttackSsrf,
            ["ssti"] = SignalKeys.AttackSsti,
            ["path_probes"] = SignalKeys.AttackPathProbe,
            ["config_exposure"] = SignalKeys.AttackConfigExposure,
            ["webshell"] = SignalKeys.AttackWebshellProbe,
            ["backup_scan"] = SignalKeys.AttackBackupScan,
            ["admin_scan"] = SignalKeys.AttackAdminScan,
            ["debug_exposure"] = SignalKeys.AttackDebugExposure,
            ["encoding_evasion"] = SignalKeys.AttackEncodingEvasion
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Map category to confidence value — avoids switch expression allocation per call.
    /// </summary>
    private FrozenDictionary<string, double>? _categoryConfidenceMap;

    private FrozenDictionary<string, double> CategoryConfidenceMap =>
        _categoryConfidenceMap ??= BuildCategoryConfidenceMap();

    private FrozenDictionary<string, double> BuildCategoryConfidenceMap() =>
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["sqli"] = GetParam("sqli_confidence", 0.95),
            ["xss"] = GetParam("xss_confidence", 0.90),
            ["traversal"] = GetParam("traversal_confidence", 0.90),
            ["cmdi"] = GetParam("cmdi_confidence", 0.95),
            ["ssrf"] = GetParam("ssrf_confidence", 0.90),
            ["ssti"] = GetParam("ssti_confidence", 0.90),
            ["path_probes"] = GetParam("path_probe_confidence", 0.75),
            ["config_exposure"] = GetParam("config_exposure_confidence", 0.80),
            ["webshell"] = GetParam("webshell_probe_confidence", 0.85),
            ["backup_scan"] = GetParam("backup_scan_confidence", 0.75),
            ["admin_scan"] = GetParam("admin_scan_confidence", 0.70),
            ["debug_exposure"] = GetParam("debug_exposure_confidence", 0.80),
            ["encoding_evasion"] = GetParam("encoding_evasion_confidence", 0.85),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Encoding evasion markers — searched via IndexOf (no regex needed)
    private static readonly string[] EncodingEvasionMarkers =
    [
        "%25",   // Double encoding
        "%00",   // Null bytes
        "%c0%af", // Overlong UTF-8 for '/'
        "%c0%ae", // Overlong UTF-8 for '.'
        "%uff0e", // Fullwidth period
        "%uff0f"  // Fullwidth solidus
    ];

    /// <summary>
    ///     Characters that MUST be present in a URL for any injection attack to work.
    ///     If none of these appear, we can skip all regex scanning. Uses SearchValues
    ///     for SIMD-accelerated scanning.
    /// </summary>
    private static readonly SearchValues<char> SuspiciousChars =
        SearchValues.Create("'\"<>;|`${}()%\\");

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = state.HttpContext.Request.Path.Value;
            var queryString = state.HttpContext.Request.QueryString.Value;

            // Fast exit: if no path and no query, nothing to scan
            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(queryString))
                return EmptyResult;

            // === Phase 1: Path-only checks (zero allocation — span-based) ===
            // These run BEFORE we concatenate path+query, avoiding string alloc for clean paths
            var pathSpan = path.AsSpan();
            var pathPatterns = EnsureCachedPathPatterns();
            var matchedFlags = 0u;

            if (MatchesAnyPathPattern(pathSpan, pathPatterns.PathProbes))
                matchedFlags |= CategoryFlag.PathProbes;
            if (MatchesAnyPathPattern(pathSpan, pathPatterns.Webshell))
                matchedFlags |= CategoryFlag.Webshell;
            if (MatchesAnyPathPattern(pathSpan, pathPatterns.BackupScan))
                matchedFlags |= CategoryFlag.BackupScan;
            if (MatchesAnyPathPattern(pathSpan, pathPatterns.AdminScan))
                matchedFlags |= CategoryFlag.AdminScan;
            if (MatchesAnyPathPattern(pathSpan, pathPatterns.DebugExposure))
                matchedFlags |= CategoryFlag.DebugExposure;
            if (MatchesAnyPathPattern(pathSpan, pathPatterns.ConfigExposure))
                matchedFlags |= CategoryFlag.ConfigExposure;

            // === Phase 2: Fast-reject for regex scanning ===
            // SIMD scan for any suspicious char in path+query. If clean, skip all regex + encoding checks.
            var hasQueryString = !string.IsNullOrEmpty(queryString);
            var needsRegexScan = pathSpan.ContainsAny(SuspiciousChars) ||
                                 (hasQueryString && queryString.AsSpan().ContainsAny(SuspiciousChars));

            if (needsRegexScan)
            {
                // Build combined input for regex — only allocates when suspicious chars found
                var input = hasQueryString
                    ? string.Concat(path ?? "/", queryString)
                    : path ?? "/";

                var maxLen = MaxInputLength;
                if (input.Length > maxLen)
                    input = input[..maxLen];

                // Regex-based injection categories
                var patterns = EnsureCompiledPatterns();
                foreach (var entry in patterns.Categories)
                {
                    foreach (var regex in entry.Patterns)
                    {
                        try
                        {
                            if (regex.IsMatch(input))
                            {
                                matchedFlags |= entry.Flag;
                                break;
                            }
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            _logger.LogDebug("Regex timeout in category {Category}", entry.Name);
                        }
                    }
                }

                // Encoding evasion — only when % is present (already covered by SuspiciousChars)
                if (HasEncodingEvasion(input))
                    matchedFlags |= CategoryFlag.EncodingEvasion;
            }

            // Fast path: nothing detected — zero allocation return
            if (matchedFlags == 0)
                return EmptyResult;

            // === Slow path: attack detected — allocations acceptable here ===
            return Task.FromResult(BuildAttackContributions(state, path!, matchedFlags));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in HaxxorContributor");
            return EmptyResult;
        }
    }

    /// <summary>
    ///     Build contributions for detected attack patterns.
    ///     Only called when matchedFlags != 0 (attack detected), so allocations here are fine.
    /// </summary>
    private IReadOnlyList<DetectionContribution> BuildAttackContributions(
        BlackboardState state, string path, uint matchedFlags)
    {
        // Expand flags to category names
        var uniqueCategories = CategoryFlag.Expand(matchedFlags);
        var categoryCount = uniqueCategories.Count;

        // Write signals
        state.WriteSignal(SignalKeys.AttackDetected, true);
        state.WriteSignal(SignalKeys.AttackCategories, string.Join(",", uniqueCategories));

        foreach (var cat in uniqueCategories)
        {
            if (CategorySignalKeys.TryGetValue(cat, out var signalKey))
                state.WriteSignal(signalKey, true);
        }

        // Determine severity
        var hasInjection = false;
        foreach (var cat in uniqueCategories)
        {
            if (InjectionCategories.Contains(cat))
            {
                hasInjection = true;
                break;
            }
        }

        var severity = (categoryCount, hasInjection) switch
        {
            ( >= 3, true) => "critical",
            ( >= 2, true) => "high",
            (_, true) => "high",
            ( >= 3, _) => "medium",
            _ => "low"
        };
        state.WriteSignal(SignalKeys.AttackSeverity, severity);

        // Compute compound confidence
        var baseConfidence = GetHighestCategoryConfidence(uniqueCategories);
        var compoundBonus = (categoryCount - 1) * CompoundBonusPerCategory;
        var finalConfidence = Math.Min(baseConfidence + compoundBonus, MaxCompoundConfidence);

        var botType = hasInjection ? BotType.MaliciousBot : BotType.Scraper;
        var categoryNames = string.Join(", ", uniqueCategories);
        var reason = hasInjection
            ? $"Attack payload detected: {categoryNames} (severity: {severity})"
            : $"Scanning pattern detected: {categoryNames} (severity: {severity})";

        _logger.LogWarning(
            "Attack detected on {Path}: categories=[{Categories}], severity={Severity}, confidence={Confidence:F2}",
            path, categoryNames, severity, finalConfidence);

        DetectionContribution contribution;
        if (hasInjection)
        {
            contribution = StrongBotContribution(
                "Attack",
                reason,
                botType: botType.ToString(),
                botName: $"Attack:{severity}") with
            {
                ConfidenceDelta = finalConfidence,
                Weight = WeightBase * WeightVerified
            };
        }
        else
        {
            contribution = BotContribution(
                "Attack",
                reason,
                confidenceOverride: finalConfidence,
                weightMultiplier: 1.5,
                botType: botType.ToString());
        }

        return [contribution];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double GetHighestCategoryConfidence(IReadOnlyList<string> categories)
    {
        var map = CategoryConfidenceMap;
        var max = 0.0;
        foreach (var cat in categories)
        {
            if (map.TryGetValue(cat, out var conf) && conf > max)
                max = conf;
        }
        return max > 0.0 ? max : 0.7; // Fallback for unknown categories
    }

    /// <summary>
    ///     Path matching using pre-lowered patterns. Uses OrdinalIgnoreCase on the path
    ///     span to avoid allocating a lowered copy of the request path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesAnyPathPattern(ReadOnlySpan<char> path, CachedPathEntry[] patterns)
    {
        foreach (ref readonly var entry in patterns.AsSpan())
        {
            if (entry.IsPrefix)
            {
                if (path.StartsWith(entry.Pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                if (path.Equals(entry.Pattern, StringComparison.OrdinalIgnoreCase) ||
                    (path.Length > entry.Pattern.Length &&
                     path.StartsWith(entry.Pattern, StringComparison.OrdinalIgnoreCase) &&
                     path[entry.Pattern.Length] == '/'))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    ///     Detect encoding evasion patterns using IndexOf — no regex needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasEncodingEvasion(string input)
    {
        foreach (var marker in EncodingEvasionMarkers)
        {
            if (input.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    ///     Get or compile regex patterns from YAML config.
    ///     Compiled on first use and cached for the lifetime of the contributor.
    /// </summary>
    private CompiledPatternSet EnsureCompiledPatterns()
    {
        var existing = _compiledPatterns;
        if (existing != null)
            return existing;

        var timeout = TimeSpan.FromMilliseconds(RegexTimeoutMs);
        var categories = new List<CompiledCategory>();

        CompileCategory(categories, "sqli", "sqli_patterns", CategoryFlag.Sqli, timeout);
        CompileCategory(categories, "xss", "xss_patterns", CategoryFlag.Xss, timeout);
        CompileCategory(categories, "traversal", "traversal_patterns", CategoryFlag.Traversal, timeout);
        CompileCategory(categories, "cmdi", "cmdi_patterns", CategoryFlag.Cmdi, timeout);
        CompileCategory(categories, "ssrf", "ssrf_patterns", CategoryFlag.Ssrf, timeout);
        CompileCategory(categories, "ssti", "ssti_patterns", CategoryFlag.Ssti, timeout);

        var result = new CompiledPatternSet(categories.ToArray());
        _compiledPatterns = result;
        return result;
    }

    private void CompileCategory(
        List<CompiledCategory> list,
        string categoryName,
        string paramName,
        uint flag,
        TimeSpan timeout)
    {
        var patterns = GetStringListParam(paramName);
        var regexList = new List<Regex>(patterns.Count);

        foreach (var pattern in patterns)
        {
            try
            {
                regexList.Add(new Regex(pattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.NonBacktracking,
                    timeout));
            }
            catch (Exception)
            {
                // NonBacktracking may not support all patterns — fall back to compiled only
                try
                {
                    regexList.Add(new Regex(pattern,
                        RegexOptions.IgnoreCase | RegexOptions.Compiled,
                        timeout));
                }
                catch (RegexParseException rex)
                {
                    _logger.LogWarning(rex, "Invalid regex in {Category}: {Pattern}", categoryName, pattern);
                }
            }
        }

        if (regexList.Count > 0)
            list.Add(new CompiledCategory(categoryName, flag, regexList.ToArray()));
    }

    /// <summary>
    ///     Build cached, pre-lowered path pattern arrays from YAML config.
    ///     Called once and cached for the contributor lifetime.
    /// </summary>
    private CachedPathPatterns EnsureCachedPathPatterns()
    {
        var existing = _cachedPathPatterns;
        if (existing != null)
            return existing;

        var result = new CachedPathPatterns(
            BuildPathEntries("path_probes"),
            BuildPathEntries("webshell_patterns"),
            BuildPathEntries("backup_patterns"),
            BuildPathEntries("admin_patterns"),
            BuildPathEntries("debug_patterns"),
            BuildPathEntries("config_patterns"));

        _cachedPathPatterns = result;
        return result;
    }

    private CachedPathEntry[] BuildPathEntries(string paramName)
    {
        var patterns = GetStringListParam(paramName);
        var entries = new CachedPathEntry[patterns.Count];
        for (var i = 0; i < patterns.Count; i++)
        {
            var p = patterns[i];
            var isPrefix = p.EndsWith('*');
            entries[i] = new CachedPathEntry(
                isPrefix ? p[..^1] : p,
                isPrefix);
        }
        return entries;
    }

    // ===== Internal types — struct-based to avoid heap allocations =====

    /// <summary>
    ///     Bit flags for category matching — avoids List allocation and deduplication.
    /// </summary>
    private static class CategoryFlag
    {
        public const uint Sqli = 1 << 0;
        public const uint Xss = 1 << 1;
        public const uint Traversal = 1 << 2;
        public const uint Cmdi = 1 << 3;
        public const uint Ssrf = 1 << 4;
        public const uint Ssti = 1 << 5;
        public const uint PathProbes = 1 << 6;
        public const uint Webshell = 1 << 7;
        public const uint BackupScan = 1 << 8;
        public const uint AdminScan = 1 << 9;
        public const uint DebugExposure = 1 << 10;
        public const uint ConfigExposure = 1 << 11;
        public const uint EncodingEvasion = 1 << 12;

        private static readonly (uint Flag, string Name)[] All =
        [
            (Sqli, "sqli"),
            (Xss, "xss"),
            (Traversal, "traversal"),
            (Cmdi, "cmdi"),
            (Ssrf, "ssrf"),
            (Ssti, "ssti"),
            (PathProbes, "path_probes"),
            (Webshell, "webshell"),
            (BackupScan, "backup_scan"),
            (AdminScan, "admin_scan"),
            (DebugExposure, "debug_exposure"),
            (ConfigExposure, "config_exposure"),
            (EncodingEvasion, "encoding_evasion")
        ];

        public static List<string> Expand(uint flags)
        {
            var result = new List<string>(4); // Most attacks have 1-3 categories
            foreach (var (flag, name) in All)
            {
                if ((flags & flag) != 0)
                    result.Add(name);
            }
            return result;
        }
    }

    /// <summary>
    ///     Pre-compiled regex category with its bit flag. Stored as array for cache-friendly iteration.
    /// </summary>
    private readonly record struct CompiledCategory(string Name, uint Flag, Regex[] Patterns);

    /// <summary>
    ///     Immutable compiled pattern set — all regex categories.
    /// </summary>
    private sealed class CompiledPatternSet(CompiledCategory[] categories)
    {
        public CompiledCategory[] Categories { get; } = categories;
    }

    /// <summary>
    ///     Pre-lowered path pattern entry — avoids per-request ToLowerInvariant.
    /// </summary>
    private readonly record struct CachedPathEntry(string Pattern, bool IsPrefix);

    /// <summary>
    ///     All cached path pattern arrays — built once from YAML config.
    /// </summary>
    private sealed class CachedPathPatterns(
        CachedPathEntry[] pathProbes,
        CachedPathEntry[] webshell,
        CachedPathEntry[] backupScan,
        CachedPathEntry[] adminScan,
        CachedPathEntry[] debugExposure,
        CachedPathEntry[] configExposure)
    {
        public CachedPathEntry[] PathProbes { get; } = pathProbes;
        public CachedPathEntry[] Webshell { get; } = webshell;
        public CachedPathEntry[] BackupScan { get; } = backupScan;
        public CachedPathEntry[] AdminScan { get; } = adminScan;
        public CachedPathEntry[] DebugExposure { get; } = debugExposure;
        public CachedPathEntry[] ConfigExposure { get; } = configExposure;
    }
}
