using System.Reflection;
using System.Text.RegularExpressions;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Parser driven by uap-core's <c>regexes.yaml</c> (the community-maintained YAML
///     regex database used by Mozilla, GitHub, Yelp and ~every UA library across languages;
///     Apache 2.0, https://github.com/ua-parser/uap-core). The file ships embedded.
///
///     Handles three of uap-core's sections:
///         user_agent_parsers  → <see cref="Parse(string)"/> returns (Family, Version)
///         os_parsers          → <see cref="Os(string)"/> returns (Family, Version)
///         device_parsers      → <see cref="Device(string)"/> returns (Family, Brand, Model)
///
///     A small companion file (<c>Data/UserAgents/ua-categories.yaml</c>) maps the
///     family strings uap-core emits to coarse "browser" / "tool" categories for
///     dashboard analytics. Bots come from <see cref="Definitions.BotPatterns"/>
///     and are intentionally absent from the category file.
///
///     The hand-rolled switch this replaces in <see cref="UserAgentParser"/> had a
///     handful of correctness bugs (Applebot → "Safari", Twitterbot / Mastodon /
///     Iceshrimp / Pleroma → "Other", Brave matched on a literal that modern Brave
///     does not put in the UA). uap-core fixes the first two out of the box; the
///     fediverse gap remains until upstream lands those rules or we supplement.
/// </summary>
public sealed class UapCoreUserAgentParser
{
    // Per-rule regex match timeout. MUST be declared before Default below: Default's static
    // initializer calls LoadEmbedded which reads RegexMatchTimeout to pass to Regex compile.
    // C# initializes static fields in declaration order, so swapping the order would mean
    // Regex sees TimeSpan.Zero (invalid) and every rule compile throws ArgumentOutOfRangeException.
    private static TimeSpan _regexMatchTimeout = TimeSpan.FromMilliseconds(100);

    // Volatile field swap: future update services can replace the rule sets atomically.
    // In-flight parses use the rules they captured at entry; subsequent parses see the new set.
    private RuleSet _rules;

    /// <summary>Singleton parser bootstrapped from the embedded uap-core file + ua-categories.yaml.</summary>
    public static UapCoreUserAgentParser Default { get; } = LoadEmbedded();

    private UapCoreUserAgentParser(RuleSet rules)
    {
        _rules = rules;
    }

    /// <summary>Counts for the three sections (telemetry / startup log only).</summary>
    public (int UserAgent, int Os, int Device) RuleCounts
    {
        get
        {
            var r = System.Threading.Volatile.Read(ref _rules);
            return (r.UserAgent.Count, r.Os.Count, r.Device.Count);
        }
    }

    /// <summary>
    ///     uap-core user_agent_parsers match. Walks rules in document order and
    ///     returns the first match's (Family, Version). Returns null when no rule
    ///     matches; callers map that to "Other".
    /// </summary>
    public (string Family, string? Version)? Parse(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var rules = System.Threading.Volatile.Read(ref _rules).UserAgent;
        return MatchFirst(rules, userAgent, out var family) ? family : null;
    }

    /// <summary>Family-only convenience used by the <c>hdr.ua_family</c> vector dim.</summary>
    public string? Family(string? userAgent) => Parse(userAgent)?.Family;

    /// <summary>
    ///     uap-core os_parsers match. Returns (Family, Version) for the OS the UA
    ///     advertises; the family is normalised to the value expected by callers
    ///     ("macOS" rather than uap-core's "Mac OS X").
    /// </summary>
    public (string Family, string? Version)? Os(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var rules = System.Threading.Volatile.Read(ref _rules).Os;
        if (!MatchFirst(rules, userAgent, out var os)) return null;
        var family = NormaliseOsFamily(os.Family);
        return (family, os.Version);
    }

    /// <summary>
    ///     uap-core device_parsers match. Returns (Family, Brand, Model) for the
    ///     device that issued the request, or null when no device rule matches
    ///     (most desktop browsers fall here; uap-core does not invent a desktop
    ///     family unless the UA carries a real desktop token).
    /// </summary>
    public (string Family, string? Brand, string? Model)? Device(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var rules = System.Threading.Volatile.Read(ref _rules).Device;
        return MatchFirstDevice(rules, userAgent);
    }

    /// <summary>
    ///     Map the device family to one of "Desktop", "Mobile", "Tablet", or null.
    ///     The classification uses only the uap-core device family string (no UA
    ///     substring tests in our code); the rule set is small and stable.
    /// </summary>
    public string? DeviceClass(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var device = Device(userAgent);
        if (device is null) return null;
        var family = device.Value.Family;
        if (string.IsNullOrEmpty(family)) return null;

        // The three families uap-core uses to surface "desktop" sit outside the
        // device_parsers normal output (they are explicit catch-alls). Everything
        // else uap-core emits is either an explicit phone (mobile) or tablet model.
        if (family.Equals("Other", StringComparison.OrdinalIgnoreCase)
            || family.Equals("Desktop", StringComparison.OrdinalIgnoreCase)
            || family.Equals("Mac", StringComparison.OrdinalIgnoreCase))
            return "Desktop";
        if (family.Contains("Tablet", StringComparison.OrdinalIgnoreCase)
            || family.Equals("iPad", StringComparison.OrdinalIgnoreCase))
            return "Tablet";
        return "Mobile";
    }

    /// <summary>
    ///     Look up the coarse browser / tool category for a uap-core family string.
    ///     Reads <c>Data/UserAgents/ua-categories.yaml</c>; returns null when the
    ///     family is unknown to that file (bots, fediverse software, unrecognised
    ///     UAs all fall here and the caller decides how to label them).
    /// </summary>
    public string? Category(string? family)
    {
        if (string.IsNullOrEmpty(family)) return null;
        var map = System.Threading.Volatile.Read(ref _rules).Categories;
        return map.TryGetValue(family, out var category) ? category : null;
    }

    /// <summary>
    ///     Replace this parser's rule set, used by a future background update service
    ///     after a fresh upstream fetch. The swap is atomic.
    /// </summary>
    public void ReplaceRules(RuleSet rules)
    {
        System.Threading.Volatile.Write(ref _rules, rules);
    }

    // ── Matching helpers ──────────────────────────────────────────────────────

    private static bool MatchFirst(
        IReadOnlyList<UaRule> rules, string ua, out (string Family, string? Version) hit)
    {
        foreach (var rule in rules)
        {
            Match match;
            try { match = rule.Regex.Match(ua); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;

            var family  = ResolveField(rule.FamilyReplacement, match, captureGroupFallback: 1) ?? string.Empty;
            var major   = ResolveField(rule.V1Replacement,    match, captureGroupFallback: 2);
            var minor   = ResolveField(rule.V2Replacement,    match, captureGroupFallback: 3);
            var patch   = ResolveField(rule.V3Replacement,    match, captureGroupFallback: 4);
            hit = (family, ComposeVersion(major, minor, patch));
            return true;
        }
        hit = default;
        return false;
    }

    private static (string Family, string? Brand, string? Model)? MatchFirstDevice(
        IReadOnlyList<DeviceRule> rules, string ua)
    {
        foreach (var rule in rules)
        {
            Match match;
            try { match = rule.Regex.Match(ua); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;

            var family = ResolveField(rule.DeviceReplacement, match, captureGroupFallback: 1) ?? string.Empty;
            var brand  = ResolveField(rule.BrandReplacement,  match, captureGroupFallback: 2);
            var model  = ResolveField(rule.ModelReplacement,  match, captureGroupFallback: 3);
            return (family, brand, model);
        }
        return null;
    }

    /// <summary>
    ///     uap-core's replacement-field semantics: when a replacement is present,
    ///     expand its $1..$9 backrefs against the match; otherwise fall back to
    ///     the corresponding capture group. Returns null for empty fields so the
    ///     caller can distinguish "no capture" from "captured an empty string".
    /// </summary>
    private static string? ResolveField(string? replacement, Match match, int captureGroupFallback)
    {
        if (!string.IsNullOrEmpty(replacement))
        {
            var expanded = BackrefRegex.Replace(replacement, m =>
            {
                var idx = int.Parse(m.Groups[1].Value);
                return idx < match.Groups.Count ? match.Groups[idx].Value : string.Empty;
            }).Trim();
            return expanded.Length == 0 ? null : expanded;
        }
        if (captureGroupFallback < match.Groups.Count)
        {
            var g = match.Groups[captureGroupFallback].Value;
            return string.IsNullOrEmpty(g) ? null : g;
        }
        return null;
    }

    private static string? ComposeVersion(string? major, string? minor, string? patch)
    {
        if (major is null) return null;
        if (minor is null) return major;
        if (patch is null) return $"{major}.{minor}";
        return $"{major}.{minor}.{patch}";
    }

    /// <summary>
    ///     The OS family strings uap-core emits do not match what older callers
    ///     (FingerprintNameComposer, BotPatternLoader) expect for display. Rather
    ///     than ask every caller to update, normalise the family at the parser
    ///     boundary. The map is intentionally minimal: only families where
    ///     uap-core's output disagrees with the existing callers.
    /// </summary>
    private static string NormaliseOsFamily(string family) => family switch
    {
        "Mac OS X" => "macOS",
        _ => family,
    };

    // ── Embedded loaders ──────────────────────────────────────────────────────

    private static UapCoreUserAgentParser LoadEmbedded()
    {
        var assembly = typeof(UapCoreUserAgentParser).Assembly;

        var regexes = LoadYaml<UapCoreYaml>(assembly, "uap-regexes.yaml") ?? new UapCoreYaml();
        var categories = LoadYaml<UaCategoriesYaml>(assembly, "ua-categories.yaml")
                         ?? new UaCategoriesYaml();

        var ruleSet = new RuleSet(
            UserAgent: CompileUa(regexes.UserAgentParsers, useOsFields: false),
            Os:        CompileUa(regexes.OsParsers,        useOsFields: true),
            Device:    CompileDevice(regexes.DeviceParsers),
            Categories: categories.Families is { Count: > 0 }
                ? new Dictionary<string, string>(categories.Families, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal));

        return new UapCoreUserAgentParser(ruleSet);
    }

    private static T? LoadYaml<T>(Assembly assembly, string filenameSuffix) where T : class
    {
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(filenameSuffix, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        try { return YamlSerializer.Deserialize<T>(ms.ToArray()); }
        catch (Exception ex)
        {
            // Surface the failure to stderr; the singleton load happens at first
            // UA parse, far from any ILogger, so this is the only chance to see it.
            Console.Error.WriteLine($"[UapCoreUserAgentParser] Failed to load {filenameSuffix}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<UaRule> CompileUa(List<UapCoreUserAgentEntryYaml>? entries, bool useOsFields)
    {
        if (entries is null) return Array.Empty<UaRule>();
        var compiled = new List<UaRule>(entries.Count);
        foreach (var entry in entries)
        {
            var regex = CompileRegex(entry.Regex);
            if (regex is null) continue;
            var family = useOsFields ? entry.OsReplacement   : entry.FamilyReplacement;
            var v1     = useOsFields ? entry.OsV1Replacement : entry.V1Replacement;
            var v2     = useOsFields ? entry.OsV2Replacement : entry.V2Replacement;
            var v3     = useOsFields ? entry.OsV3Replacement : entry.V3Replacement;
            compiled.Add(new UaRule(regex, family, v1, v2, v3));
        }
        return compiled;
    }

    private static IReadOnlyList<DeviceRule> CompileDevice(List<UapCoreDeviceEntryYaml>? entries)
    {
        if (entries is null) return Array.Empty<DeviceRule>();
        var compiled = new List<DeviceRule>(entries.Count);
        foreach (var entry in entries)
        {
            // device_parsers can carry a regex_flag: 'i' (case-insensitive) in addition to
            // the pattern; honour it so iPhone/iPad rules match regardless of UA casing.
            var regex = CompileRegex(entry.Regex, entry.RegexFlag);
            if (regex is null) continue;
            compiled.Add(new DeviceRule(regex,
                entry.DeviceReplacement, entry.BrandReplacement, entry.ModelReplacement));
        }
        return compiled;
    }

    private static Regex? CompileRegex(string? pattern, string? flag = null)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (string.Equals(flag, "i", StringComparison.OrdinalIgnoreCase))
            options |= RegexOptions.IgnoreCase;
        try
        {
            return new Regex(pattern, options, RegexMatchTimeout);
        }
        catch (Exception)
        {
            // .NET regex engine occasionally rejects a pattern that depends on
            // Ruby / Python regex extensions. Upstream is community-maintained,
            // so we skip the offender rather than fail the whole load.
            return null;
        }
    }

    /// <summary>
    ///     Per-rule regex match timeout. See <see cref="_regexMatchTimeout"/> for the
    ///     backing field. Bounded above by what a single pathological UA can spend in
    ///     one regex; the parse loop walks thousands of rules so this cap matters on
    ///     cold paths. Configurable via
    ///     <see cref="Models.UapCoreOptions.RegexMatchTimeoutMs"/>; the value is
    ///     captured by Regex.Compiled at compile time, so subsequent re-binds only
    ///     affect rules compiled afterwards.
    /// </summary>
    internal static TimeSpan RegexMatchTimeout => _regexMatchTimeout;

    /// <summary>
    ///     Replace the regex timeout used when compiling rules. Called once at
    ///     startup by the DI wiring after options are resolved; the setter is
    ///     internal so existing rules cannot have their timeout silently retconned.
    /// </summary>
    internal static void ConfigureRegexTimeout(TimeSpan timeout)
    {
        if (timeout > TimeSpan.Zero) _regexMatchTimeout = timeout;
    }

    private static readonly Regex BackrefRegex = new(@"\$([1-9])", RegexOptions.Compiled);

    public sealed record RuleSet(
        IReadOnlyList<UaRule> UserAgent,
        IReadOnlyList<UaRule> Os,
        IReadOnlyList<DeviceRule> Device,
        IReadOnlyDictionary<string, string> Categories);

    public sealed record UaRule(
        Regex Regex,
        string? FamilyReplacement,
        string? V1Replacement,
        string? V2Replacement,
        string? V3Replacement);

    public sealed record DeviceRule(
        Regex Regex,
        string? DeviceReplacement,
        string? BrandReplacement,
        string? ModelReplacement);
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class UapCoreYaml
{
    public List<UapCoreUserAgentEntryYaml>? UserAgentParsers { get; set; }
    public List<UapCoreUserAgentEntryYaml>? OsParsers { get; set; }
    public List<UapCoreDeviceEntryYaml>? DeviceParsers { get; set; }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class UapCoreUserAgentEntryYaml
{
    public string? Regex { get; set; }

    // user_agent_parsers fields
    public string? FamilyReplacement { get; set; }
    public string? V1Replacement { get; set; }
    public string? V2Replacement { get; set; }
    public string? V3Replacement { get; set; }
    public string? V4Replacement { get; set; }

    // os_parsers fields (same row shape, different field prefix); the consumer
    // (Parse vs Os) picks whichever pair is populated for the section it owns.
    public string? OsReplacement { get; set; }
    public string? OsV1Replacement { get; set; }
    public string? OsV2Replacement { get; set; }
    public string? OsV3Replacement { get; set; }
    public string? OsV4Replacement { get; set; }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class UapCoreDeviceEntryYaml
{
    public string? Regex { get; set; }
    public string? RegexFlag { get; set; }
    public string? DeviceReplacement { get; set; }
    public string? BrandReplacement { get; set; }
    public string? ModelReplacement { get; set; }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class UaCategoriesYaml
{
    public Dictionary<string, string>? Families { get; set; }
}