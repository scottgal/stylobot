using System.Buffers;
using System.Text.RegularExpressions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Definitions.WellKnownBots;

/// <summary>
///     Thread-safe, atomically-replaced index of the arcjet well-known-bots catalog.
///
///     Three-tier lookup on each UA string:
///     <list type="number">
///       <item>
///         <b>L1 SIMD pre-filter</b> — <see cref="SearchValues{T}"/> Aho-Corasick scan of
///         the entire UA in O(|UA|). 81 % of arcjet patterns are pure literals; extracted
///         cores cover the complex ones. A miss here returns null with zero allocations.
///       </item>
///       <item>
///         <b>L2 literal fast path</b> — <c>string.Contains(OrdinalIgnoreCase)</c> for the
///         81 % of patterns that have no regex metacharacters. No Regex object involved.
///       </item>
///       <item>
///         <b>L3 regex</b> — <see cref="Regex"/> (NonBacktracking preferred, Compiled
///         fallback) for the remaining ~19 % of patterns.
///       </item>
///     </list>
///
///     Scan results (positive and null) are cached via <see cref="BoundedCache{TKey,TValue}"/>
///     (LFU, 4 000-entry cap) so the full three-tier scan runs at most once per unique UA
///     string per catalog lifetime. Cleared on <see cref="Replace"/> so stale negatives
///     never survive a catalog refresh.
///
///     The static <see cref="Default"/> singleton is shared by callers that live in static
///     contexts (<c>FingerprintNameComposer</c>, <c>BotPatternLoader</c>). The DI-registered
///     instance is the same object.
/// </summary>
public sealed class WellKnownBotIndex
{
    public static readonly WellKnownBotIndex Default = new();

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
    private const int MinCoreLength = 4;

    // SIMD-backed metacharacter detector — used at Replace() time to classify patterns.
    private static readonly SearchValues<char> MetaChars =
        SearchValues.Create(@"\.+*?[]{}()|^$");

    private volatile IndexEntry[] _entries = [];
    // Plain field: reference writes are atomic on all .NET platforms; volatile
    // is unnecessary for a field that's only written once per Replace() call.
    private SearchValues<string>? _preFilter;

    private BoundedCache<string, WellKnownBotMatch?> _scanCache =
        new(maxSize: 4_000, defaultTtl: TimeSpan.FromMinutes(30));

    public int Count => _entries.Length;

    public string? TryFindBotTypeByDisplayName(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return null;
        foreach (var e in _entries)
            if (string.Equals(e.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                return e.BotType.ToString();
        return null;
    }

    public IEnumerable<(string Id, string DisplayName, string BotType)> EnumerateForArchetypePromotion()
    {
        foreach (var e in _entries)
            yield return (e.Id, e.DisplayName, e.BotType.ToString());
    }

    /// <summary>Atomically replaces the index from a freshly-downloaded bot list.</summary>
    public void Replace(IReadOnlyList<WellKnownBotEntry> entries)
    {
        var compiled = new List<IndexEntry>(entries.Count);
        var keywords = new List<string>(entries.Count * 2);

        foreach (var e in entries)
        {
            var (litAcc, reAcc) = ClassifyPatterns(e.Pattern.Accepted, keywords);
            if (litAcc.Length == 0 && reAcc.Length == 0) continue;
            var (litFbid, reFbid) = ClassifyPatterns(e.Pattern.Forbidden, null);
            compiled.Add(new IndexEntry(
                e.Id, MapBotType(e.Categories), DisplayName(e.Id),
                litAcc, reAcc, litFbid, reFbid));
        }

        _entries = compiled.ToArray();
        _preFilter = keywords.Count > 0
            ? SearchValues.Create(keywords.ToArray(), StringComparison.OrdinalIgnoreCase)
            : null;
        _scanCache.Clear();
    }

    /// <summary>
    ///     Returns a match for the first arcjet entry whose accepted patterns match
    ///     <paramref name="userAgent"/> and none of whose forbidden patterns match.
    ///     Returns null when no entry matches or the index is not yet loaded.
    /// </summary>
    public WellKnownBotMatch? TryMatch(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var entries = _entries;
        if (entries.Length == 0) return null;

        if (_scanCache.TryGet(userAgent, out var cached)) return cached;

        // L1: single O(|UA|) Aho-Corasick scan — if none of the ~600 bot keywords
        // appear in this UA, no entry can possibly match. Most human Chrome/Safari
        // UAs exit here with zero allocations.
        var filter = _preFilter;
        if (filter != null && !userAgent.AsSpan().ContainsAny(filter))
        {
            _scanCache.Set(userAgent, null);
            return null;
        }

        // L2 + L3: per-entry check, literals before regex.
        WellKnownBotMatch? result = null;
        foreach (var e in entries)
        {
            if (!MatchesAccepted(e, userAgent)) continue;
            if (MatchesForbidden(e, userAgent)) continue;
            result = new WellKnownBotMatch(e.Id, e.BotType, e.DisplayName);
            break;
        }

        _scanCache.Set(userAgent, result);
        return result;
    }

    // ── internals ────────────────────────────────────────────────────────────

    private static bool MatchesAccepted(IndexEntry e, string ua)
    {
        foreach (var lit in e.LiteralAccepted)
            if (ua.Contains(lit, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var re in e.RegexAccepted)
        {
            // Required-core pre-check: an unanchored IsMatch can only succeed if the
            // pattern's leading literal appears in the UA. A cheap Contains skips the
            // expensive (interpreted-under-AOT) regex for the ~99% of entries whose
            // core is absent. Core is null when no required leading literal exists
            // (pattern starts with a metachar) — then the regex always runs. Order and
            // match result are unchanged; only skipped regexes are ones that can't match.
            if (re.Core != null && !ua.Contains(re.Core, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (re.Re.IsMatch(ua)) return true; }
            catch (RegexMatchTimeoutException) { /* skip */ }
        }
        return false;
    }

    private static bool MatchesForbidden(IndexEntry e, string ua)
    {
        if (e.LiteralForbidden.Length == 0 && e.RegexForbidden.Length == 0) return false;
        foreach (var lit in e.LiteralForbidden)
            if (ua.Contains(lit, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var re in e.RegexForbidden)
        {
            if (re.Core != null && !ua.Contains(re.Core, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (re.Re.IsMatch(ua)) return true; }
            catch (RegexMatchTimeoutException) { /* skip */ }
        }
        return false;
    }

    /// <summary>
    ///     A compiled regex paired with its required leading-literal <see cref="Core"/>
    ///     (the run of literal characters before the first metacharacter, dropped when
    ///     that char is a <c>?</c>/<c>*</c>/<c>{0,…}</c> quantifier). For an unanchored
    ///     <c>IsMatch</c> the core must appear in the input, so a cheap
    ///     <c>Contains(Core)</c> soundly skips regexes that cannot match. <see cref="Core"/>
    ///     is null when no such required prefix exists (pattern starts with a metachar).
    /// </summary>
    private readonly record struct RegexEntry(Regex Re, string? Core);

    /// <summary>
    ///     Leading run of literal characters that MUST appear for <paramref name="pattern"/>
    ///     to match under an unanchored <c>IsMatch</c>. Conservative: returns null (regex
    ///     always runs) when the required prefix is shorter than <see cref="MinCoreLength"/>
    ///     or the pattern opens with a metacharacter/group.
    /// </summary>
    private static string? ExtractRequiredCore(string pattern)
    {
        // The leading run of literal characters before the first metacharacter. For an
        // unanchored IsMatch that run MUST appear in the input, so Contains(core) soundly
        // skips a regex that cannot match. Null when the pattern opens with a metacharacter
        // (anchor "^…", group "(…", class "[…") or the run is shorter than MinCoreLength —
        // then the regex always runs.
        //
        // Soundness rests on one invariant: the leading literal is genuinely REQUIRED, i.e.
        // no top-level alternation ("a|b" also matches "b") and no quantifier on the run's
        // last char ("foo?bar"). This is the SAME bet the L1 literal-core pre-filter already
        // makes (ExtractLiteralCore + _preFilter globally early-exit on each pattern's longest
        // literal), so relying on it here adds no blind spot L1 doesn't already have. Verified
        // against the live arcjet catalog: of 116 regex patterns the first metachar is only
        // \ / [ / ( / ^ — never | or a quantifier. If that ever changes, both layers need the
        // per-branch fix together.
        var span = pattern.AsSpan();
        var i = span.IndexOfAny(MetaChars);
        if (i < 0) return span.Length >= MinCoreLength ? span.ToString() : null;
        return i >= MinCoreLength ? span[..i].ToString() : null;
    }

    /// <summary>
    ///     Separates <paramref name="patterns"/> into pure literals (no regex metacharacters)
    ///     and compiled regexes (NonBacktracking preferred). Also populates
    ///     <paramref name="keywords"/> with strings that serve as pre-filter keys: the literal
    ///     itself for pure-literal patterns, or the longest extracted literal core (≥4 chars)
    ///     for complex patterns.
    /// </summary>
    private static (string[] Literals, RegexEntry[] Regexes) ClassifyPatterns(
        string[] patterns, List<string>? keywords)
    {
        var literals = new List<string>();
        var regexes  = new List<RegexEntry>();

        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;

            if (!p.AsSpan().ContainsAny(MetaChars))
            {
                // Pure literal: ContainsAny(OrdinalIgnoreCase) is faster and zero-overhead.
                literals.Add(p);
                keywords?.Add(p);
            }
            else
            {
                // Complex pattern: compile, then extract a literal core for the pre-filter.
                Regex? re = null;
                try
                {
                    re = new Regex(p,
                        RegexOptions.Compiled | RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                        RegexTimeout);
                }
                catch (ArgumentException)
                {
                    // NonBacktracking rejects backreferences/lookaheads — fall back.
                    try
                    {
                        re = new Regex(p,
                            RegexOptions.Compiled | RegexOptions.IgnoreCase |
                            RegexOptions.CultureInvariant,
                            RegexTimeout);
                    }
                    catch (ArgumentException) { continue; }
                }

                regexes.Add(new RegexEntry(re, ExtractRequiredCore(p)));
                var core = ExtractLiteralCore(p);
                if (core != null) keywords?.Add(core);
            }
        }

        return (literals.ToArray(), regexes.ToArray());
    }

    /// <summary>
    ///     Returns the longest sequence of non-metacharacter characters in
    ///     <paramref name="pattern"/>, or null if that sequence is shorter than
    ///     <see cref="MinCoreLength"/>. Used to populate the L1 pre-filter for complex
    ///     patterns so a SIMD scan can still gate the per-regex work.
    /// </summary>
    private static string? ExtractLiteralCore(string pattern)
    {
        var span = pattern.AsSpan();
        ReadOnlySpan<char> best = default;
        while (!span.IsEmpty)
        {
            var i = span.IndexOfAny(MetaChars);
            var seg = i < 0 ? span : span[..i];
            if (seg.Length > best.Length) best = seg;
            if (i < 0) break;
            span = span[(i + 1)..];
        }
        return best.Length >= MinCoreLength ? best.ToString() : null;
    }

    private static string DisplayName(string id)
    {
        var parts = id.Split('-');
        return string.Join(" ", parts.Select(p => p.Length > 0
            ? char.ToUpperInvariant(p[0]) + p[1..]
            : p));
    }

    private static BotType MapBotType(string[] categories)
    {
        foreach (var cat in categories)
        {
            switch (cat)
            {
                case "search-engine":
                case "google":
                case "microsoft":
                case "meta":
                case "amazon":
                case "apple":
                case "yahoo":
                    return BotType.SearchEngine;
                case "ai":
                    return BotType.AiBot;
                case "social":
                    return BotType.SocialMediaBot;
                case "monitor":
                    return BotType.MonitoringBot;
                case "tool":
                case "programmatic":
                case "vercel":
                    return BotType.Tool;
                case "academic":
                case "archive":
                case "feedfetcher":
                case "optimizer":
                case "preview":
                case "advertising":
                case "webhook":
                case "slack":
                    return BotType.GoodBot;
            }
        }
        return BotType.Unknown;
    }

    private sealed record IndexEntry(
        string Id,
        BotType BotType,
        string DisplayName,
        string[] LiteralAccepted,
        RegexEntry[] RegexAccepted,
        string[] LiteralForbidden,
        RegexEntry[] RegexForbidden);
}

/// <summary>Match result from <see cref="WellKnownBotIndex.TryMatch"/>.</summary>
public sealed record WellKnownBotMatch(string Id, BotType BotType, string DisplayName);
