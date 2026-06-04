using System.Reflection;
using System.Text.RegularExpressions;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Replaces the hand-rolled browser/tool switch in <see cref="UserAgentParser"/> with a
///     parser driven by uap-core's <c>regexes.yaml</c> -- the same regex database used by
///     Mozilla, GitHub, and ~every UA library across languages
///     (https://github.com/ua-parser/uap-core, Apache 2.0). The file ships embedded; the
///     <c>UapCoreUpdateService</c> swaps the in-memory parser with the latest upstream copy
///     on a schedule so new browsers / bots / fediverse software families come in without
///     us editing a switch statement.
///
///     The previous switch knew about ~16 browsers/tools and miscategorised everything
///     else as "Other". Notable bugs it had on prod:
///         * Applebot UA contains "Safari/" + "Version/" → returned "Safari", not "Applebot".
///         * Mastodon / Iceshrimp / Pleroma / Misskey / GoToSocial UAs all returned "Other"
///           even though they're well-known fediverse software with distinct UA tokens.
///         * Brave was matched on the literal string "Brave" in the UA, but modern Brave
///           does not put "Brave" in its UA (uses Chrome UA + navigator.brave hint). uap-core
///           does not solve that one either (no server-side signal exists), but at least
///           does not silently mis-claim Chrome → Brave for any privacy-hardened Chrome.
///
///     This parser preserves the legacy <see cref="UserAgentParser.BrowserFamily"/> return
///     contract: the <c>Family</c> string is the uap-core <c>family_replacement</c> (or
///     first capture group when none is set). Callers that expected the old narrow set
///     ("Chrome", "Firefox", ...) still receive those for the common UAs because uap-core
///     emits the same family strings for them; callers that hit niche UAs now get a real
///     family name instead of "Other".
/// </summary>
public sealed class UapCoreUserAgentParser
{
    // Volatile field swap: the update service replaces the rule list atomically; in-flight
    // parses iterate whichever list they captured at entry. Acquire/release semantics keep
    // a stale list from being read after the swap.
    private IReadOnlyList<UaRule> _rules;

    /// <summary>Singleton parser bootstrapped from the embedded uap-core file.</summary>
    public static UapCoreUserAgentParser Default { get; } = LoadEmbedded();

    private UapCoreUserAgentParser(IReadOnlyList<UaRule> rules)
    {
        _rules = rules;
    }

    /// <summary>
    ///     Number of rules currently loaded. The update service uses this for the
    ///     post-swap log line so an operator can see whether the upstream file
    ///     grew / shrank between fetches.
    /// </summary>
    public int RuleCount => System.Threading.Volatile.Read(ref _rules).Count;

    /// <summary>
    ///     Walk the rules in document order (the order uap-core publishes is
    ///     significant -- specific cases come before generic catch-alls) and
    ///     return the first match as (Family, Version). Family comes from the
    ///     matched rule's <c>family_replacement</c> (or first capture group);
    ///     Version comes from the same match's v1/v2/v3 replacement fields, or
    ///     from capture groups 2/3/4 -- same convention as every uap-core
    ///     reference implementation. Returns null when no rule matches.
    /// </summary>
    public (string Family, string? Version)? Parse(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var rules = System.Threading.Volatile.Read(ref _rules);
        foreach (var rule in rules)
        {
            Match match;
            try { match = rule.Regex.Match(userAgent); }
            catch (RegexMatchTimeoutException) { continue; } // single pathological UA must not break parsing
            if (!match.Success) continue;

            var family = ResolveField(rule.FamilyReplacement, match, captureGroupFallback: 1) ?? string.Empty;
            var major  = ResolveField(rule.V1Replacement,    match, captureGroupFallback: 2);
            var minor  = ResolveField(rule.V2Replacement,    match, captureGroupFallback: 3);
            var patch  = ResolveField(rule.V3Replacement,    match, captureGroupFallback: 4);
            var version = ComposeVersion(major, minor, patch);
            return (family, version);
        }
        return null;
    }

    /// <summary>Family-only convenience for the existing <c>hdr.ua_family</c> dim.</summary>
    public string? Family(string? userAgent) => Parse(userAgent)?.Family;

    /// <summary>
    ///     uap-core's replacement-field semantics: when a v*_replacement / family_replacement
    ///     is present, expand its $1..$9 backrefs against the match; when absent, fall back
    ///     to the corresponding capture group. Returns null for empty fields so the caller
    ///     can distinguish "no version captured" from "captured an empty string".
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
    ///     Replace this parser's rule set, used by the background update service after a
    ///     fresh upstream fetch. The swap is atomic -- in-flight parses use the rules
    ///     they captured at entry and return normally; subsequent parses see the new set.
    /// </summary>
    public void ReplaceRules(IReadOnlyList<UaRule> rules)
    {
        System.Threading.Volatile.Write(ref _rules, rules);
    }

    // --- Embedded loader ---

    private static UapCoreUserAgentParser LoadEmbedded()
    {
        var assembly = typeof(UapCoreUserAgentParser).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("uap-regexes.yaml", StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return new UapCoreUserAgentParser(Array.Empty<UaRule>());

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var dto = YamlSerializer.Deserialize<UapCoreYaml>(ms.ToArray());
        return new UapCoreUserAgentParser(Compile(dto?.UserAgentParsers ?? new List<UapCoreUserAgentEntryYaml>()));
    }

    internal static IReadOnlyList<UaRule> Compile(IReadOnlyList<UapCoreUserAgentEntryYaml> entries)
    {
        var compiled = new List<UaRule>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Regex)) continue;
            Regex regex;
            try
            {
                // Compiled regexes pay a one-time cost at startup (~3-5s for 4000+ rules)
                // in exchange for materially faster matching on the hot path. A 100 ms
                // per-rule timeout caps any single ReDoS-prone pattern.
                regex = new Regex(entry.Regex, RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (Exception)
            {
                // Skip patterns that fail to compile under the .NET regex engine (rare;
                // upstream is community-maintained and very occasionally lands a regex
                // that depends on Ruby/Python-specific syntax). Logging the exact failure
                // belongs in the update service; the Default path just stays quiet.
                continue;
            }
            compiled.Add(new UaRule(regex, entry.FamilyReplacement,
                entry.V1Replacement, entry.V2Replacement, entry.V3Replacement));
        }
        return compiled;
    }

    private static readonly Regex BackrefRegex = new(@"\$([1-9])", RegexOptions.Compiled);

    public sealed record UaRule(
        Regex Regex,
        string? FamilyReplacement,
        string? V1Replacement,
        string? V2Replacement,
        string? V3Replacement);
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class UapCoreYaml
{
    public List<UapCoreUserAgentEntryYaml>? UserAgentParsers { get; set; }
}

[VYaml.Annotations.YamlObject(VYaml.Annotations.NamingConvention.SnakeCase)]
public sealed partial class UapCoreUserAgentEntryYaml
{
    public string? Regex { get; set; }
    public string? FamilyReplacement { get; set; }
    public string? V1Replacement { get; set; }
    public string? V2Replacement { get; set; }
    public string? V3Replacement { get; set; }
    public string? V4Replacement { get; set; }
}