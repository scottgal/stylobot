using System.Buffers;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that detects script-kiddie probing,
///     injection attempts (SQL / XSS / traversal / cmd / SSRF / SSTI),
///     config-file exposure scans, webshell probes, and attack-tool
///     signatures from request path + query metadata.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>HaxxorContributor</c>. Priority 7 -- pure single-request
///         analysis, no cross-request state.
///     </para>
///     <para>
///         Regex compilation + path-pattern arrays lazy-cached on the atom
///         instance. YAML supplies pattern lists per category
///         (path_probes / webshell_patterns / config_patterns /
///         admin_patterns / debug_patterns / backup_patterns +
///         sqli/xss/traversal/cmdi/ssrf/ssti). Encoding-evasion markers
///         are compiled into a small constant list.
///     </para>
///     <para>
///         SIMD SearchValues + span-based path checks preserve the
///         contributor's zero-allocation fast path for clean requests.
///     </para>
/// </remarks>
public sealed class HaxxorAtom : DetectorAtomBase
{
    private static readonly FrozenSet<string> InjectionCategories =
        new[] { "sqli", "xss", "traversal", "cmdi", "ssrf", "ssti" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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

    private static readonly string[] EncodingEvasionMarkers =
    [
        "%25", "%00", "%c0%af", "%c0%ae", "%uff0e", "%uff0f"
    ];

    private static readonly SearchValues<char> SuspiciousChars =
        SearchValues.Create("'\"<>;|`${}()%\\");

    private readonly ILogger<HaxxorAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private volatile CompiledPatternSet? _compiledPatterns;
    private volatile CachedPathPatterns? _cachedPathPatterns;
    private FrozenDictionary<string, double>? _categoryConfidenceMap;

    public HaxxorAtom(
        ILogger<HaxxorAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "Haxxor", category: "Attack")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 7;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private int MaxInputLength => _configProvider.GetParameter(Name, "max_input_length", 8192);
    private int RegexTimeoutMs => Math.Clamp(_configProvider.GetParameter(Name, "regex_timeout_ms", 100), 10, 5000);
    private double CompoundBonusPerCategory => _configProvider.GetParameter(Name, "compound_bonus_per_category", 0.05);
    private double MaxCompoundConfidence => _configProvider.GetParameter(Name, "max_compound_confidence", 0.99);
    private double WeightBase => _configProvider.GetDefaults(Name).Weights.Base;
    private double WeightVerified => _configProvider.GetDefaults(Name).Weights.Verified;

    private FrozenDictionary<string, double> CategoryConfidenceMap =>
        _categoryConfidenceMap ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["sqli"] = _configProvider.GetParameter(Name, "sqli_confidence", 0.95),
            ["xss"] = _configProvider.GetParameter(Name, "xss_confidence", 0.90),
            ["traversal"] = _configProvider.GetParameter(Name, "traversal_confidence", 0.90),
            ["cmdi"] = _configProvider.GetParameter(Name, "cmdi_confidence", 0.95),
            ["ssrf"] = _configProvider.GetParameter(Name, "ssrf_confidence", 0.90),
            ["ssti"] = _configProvider.GetParameter(Name, "ssti_confidence", 0.90),
            ["path_probes"] = _configProvider.GetParameter(Name, "path_probe_confidence", 0.75),
            ["config_exposure"] = _configProvider.GetParameter(Name, "config_exposure_confidence", 0.80),
            ["webshell"] = _configProvider.GetParameter(Name, "webshell_probe_confidence", 0.85),
            ["backup_scan"] = _configProvider.GetParameter(Name, "backup_scan_confidence", 0.75),
            ["admin_scan"] = _configProvider.GetParameter(Name, "admin_scan_confidence", 0.70),
            ["debug_exposure"] = _configProvider.GetParameter(Name, "debug_exposure_confidence", 0.80),
            ["encoding_evasion"] = _configProvider.GetParameter(Name, "encoding_evasion_confidence", 0.85)
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        try
        {
            var path = context.Request.Path.Value;
            var queryString = context.Request.QueryString.Value;

            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(queryString))
                return Task.FromResult(None());

            var pathSpan = path.AsSpan();
            var pathPatterns = EnsureCachedPathPatterns();
            var matchedFlags = 0u;

            if (MatchesAnyPathPattern(pathSpan, pathPatterns.PathProbes)) matchedFlags |= CategoryFlag.PathProbes;
            if (MatchesAnyPathPattern(pathSpan, pathPatterns.Webshell)) matchedFlags |= CategoryFlag.Webshell;
            if (MatchesAnyPathPattern(pathSpan, pathPatterns.BackupScan)) matchedFlags |= CategoryFlag.BackupScan;
            if (MatchesAnyPathPattern(pathSpan, pathPatterns.AdminScan)) matchedFlags |= CategoryFlag.AdminScan;
            if (MatchesAnyPathPattern(pathSpan, pathPatterns.DebugExposure)) matchedFlags |= CategoryFlag.DebugExposure;
            if (MatchesAnyPathPattern(pathSpan, pathPatterns.ConfigExposure)) matchedFlags |= CategoryFlag.ConfigExposure;

            var hasQueryString = !string.IsNullOrEmpty(queryString);
            var needsRegexScan = pathSpan.ContainsAny(SuspiciousChars)
                                 || (hasQueryString && queryString.AsSpan().ContainsAny(SuspiciousChars));

            if (needsRegexScan)
            {
                var input = hasQueryString ? string.Concat(path ?? "/", queryString) : path ?? "/";
                var maxLen = MaxInputLength;
                if (input.Length > maxLen) input = input[..maxLen];

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

                if (HasEncodingEvasion(input)) matchedFlags |= CategoryFlag.EncodingEvasion;
            }

            if (matchedFlags == 0) return Task.FromResult(None());

            return Task.FromResult(BuildAttackContributions(sink, sessionId, path!, matchedFlags));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in HaxxorAtom");
            return Task.FromResult(None());
        }
    }

    private IReadOnlyList<DetectionContribution> BuildAttackContributions(
        SignalSink sink, string sessionId, string path, uint matchedFlags)
    {
        var uniqueCategories = CategoryFlag.Expand(matchedFlags);
        var categoryCount = uniqueCategories.Count;

        sink.Raise($"{SignalKeys.AttackDetected}:true", sessionId);
        sink.Raise($"{SignalKeys.AttackCategories}:{string.Join(",", uniqueCategories)}", sessionId);

        foreach (var cat in uniqueCategories)
        {
            if (CategorySignalKeys.TryGetValue(cat, out var signalKey))
                sink.Raise($"{signalKey}:true", sessionId);
        }

        var hasInjection = false;
        foreach (var cat in uniqueCategories)
        {
            if (InjectionCategories.Contains(cat)) { hasInjection = true; break; }
        }

        var severity = (categoryCount, hasInjection) switch
        {
            ( >= 3, true) => "critical",
            ( >= 2, true) => "high",
            (_, true) => "high",
            ( >= 3, _) => "medium",
            _ => "low"
        };
        sink.Raise($"{SignalKeys.AttackSeverity}:{severity}", sessionId);

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

        if (hasInjection)
        {
            return Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Attack",
                ConfidenceDelta = finalConfidence,
                Weight = WeightBase * WeightVerified,
                Reason = reason,
                BotType = botType.ToString(),
                BotName = $"Attack:{severity}"
            });
        }

        return Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = "Attack",
            ConfidenceDelta = finalConfidence,
            Weight = 1.5,
            Reason = reason,
            BotType = botType.ToString()
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double GetHighestCategoryConfidence(IReadOnlyList<string> categories)
    {
        var map = CategoryConfidenceMap;
        var max = 0.0;
        foreach (var cat in categories)
        {
            if (map.TryGetValue(cat, out var conf) && conf > max) max = conf;
        }
        return max > 0.0 ? max : 0.7;
    }

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
                if (path.Equals(entry.Pattern, StringComparison.OrdinalIgnoreCase)
                    || (path.Length > entry.Pattern.Length
                        && path.StartsWith(entry.Pattern, StringComparison.OrdinalIgnoreCase)
                        && path[entry.Pattern.Length] == '/'))
                    return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasEncodingEvasion(string input)
    {
        foreach (var marker in EncodingEvasionMarkers)
        {
            if (input.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private CompiledPatternSet EnsureCompiledPatterns()
    {
        var existing = _compiledPatterns;
        if (existing is not null) return existing;

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
        List<CompiledCategory> list, string categoryName, string paramName, uint flag, TimeSpan timeout)
    {
        var patterns = GetStringListParam(paramName);
        var regexList = new List<Regex>(patterns.Count);
        foreach (var pattern in patterns)
        {
            try
            {
                regexList.Add(new Regex(pattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.NonBacktracking, timeout));
            }
            catch
            {
                try
                {
                    regexList.Add(new Regex(pattern,
                        RegexOptions.IgnoreCase | RegexOptions.Compiled, timeout));
                }
                catch (RegexParseException rex)
                {
                    _logger.LogWarning(rex, "Invalid regex in {Category}: {Pattern}", categoryName, pattern);
                }
            }
        }
        if (regexList.Count > 0) list.Add(new CompiledCategory(categoryName, flag, regexList.ToArray()));
    }

    private CachedPathPatterns EnsureCachedPathPatterns()
    {
        var existing = _cachedPathPatterns;
        if (existing is not null) return existing;
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
            entries[i] = new CachedPathEntry(isPrefix ? p[..^1] : p, isPrefix);
        }
        return entries;
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
            var result = new List<string>(4);
            foreach (var (flag, name) in All)
            {
                if ((flags & flag) != 0) result.Add(name);
            }
            return result;
        }
    }

    private readonly record struct CompiledCategory(string Name, uint Flag, Regex[] Patterns);

    private sealed class CompiledPatternSet(CompiledCategory[] categories)
    {
        public CompiledCategory[] Categories { get; } = categories;
    }

    private readonly record struct CachedPathEntry(string Pattern, bool IsPrefix);

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
