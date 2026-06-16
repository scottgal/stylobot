using System.Text.RegularExpressions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Definitions.WellKnownBots;

/// <summary>
///     Thread-safe, atomically-replaced index of the arcjet well-known-bots catalog.
///     Pre-compiles UA regex patterns on load; each request does an O(N) scan of
///     compiled regexes (N ≈ 635). Replaced atomically by
///     <see cref="WellKnownBotRefreshService"/> on each successful download.
///     <para>
///         <b>Performance:</b> scan results (positive and null/miss) are cached via
///         the canonical <see cref="BoundedCache{TKey,TValue}"/> with LFU eviction so
///         popular UAs (Chrome, Safari) pay the O(N-regex) cost only once per catalog
///         lifetime. Cache is cleared on <see cref="Replace"/> so stale negatives
///         never block entries added by a later catalog refresh.
///     </para>
///     <para>
///         The static <see cref="Default"/> singleton is shared by callers that
///         live in static contexts (e.g. <c>FingerprintNameComposer</c>,
///         <c>BotPatternLoader</c>). The DI-registered instance is the same
///         object, so DI consumers and static callers see the same data.
///     </para>
/// </summary>
public sealed class WellKnownBotIndex
{
    /// <summary>
    ///     Shared static singleton. Starts empty; populated on first successful
    ///     download by <see cref="WellKnownBotRefreshService"/>. The DI
    ///     registration returns this instance so both DI and static consumers
    ///     share the same atomically-replaced entries.
    /// </summary>
    public static readonly WellKnownBotIndex Default = new();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private volatile IndexEntry[] _entries = [];

    // UA-keyed scan result cache. Uses BoundedCache (LFU eviction, 30-min TTL)
    // per the project rule: grep for the canonical pattern before writing infra.
    // Positive hits AND confirmed misses (null) are both cached so repeat UAs
    // (human Chrome, Safari) never re-scan all 635 regexes.
    // Cleared on Replace so stale negatives don't survive a catalog refresh.
    private BoundedCache<string, WellKnownBotMatch?> _scanCache =
        new(maxSize: 4_000, defaultTtl: TimeSpan.FromMinutes(30));

    public int Count => _entries.Length;

    /// <summary>
    ///     Looks up a display name (e.g. "Google Crawler") and returns the corresponding
    ///     <see cref="BotType"/> as a string, or null when not found.
    ///     Used by <see cref="Definitions.BotPatterns.BotPatternLoader.FindBotTypeByName"/>
    ///     as a fallback for bot names that originate from this catalog rather than from YAML.
    /// </summary>
    public string? TryFindBotTypeByDisplayName(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return null;
        foreach (var e in _entries)
            if (string.Equals(e.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                return e.BotType.ToString();
        return null;
    }

    /// <summary>Atomically replaces the index from a freshly-downloaded bot list.</summary>
    public void Replace(IReadOnlyList<WellKnownBotEntry> entries)
    {
        var compiled = new List<IndexEntry>(entries.Count);
        foreach (var e in entries)
        {
            var accepted = CompilePatterns(e.Pattern.Accepted);
            if (accepted.Length == 0) continue;
            compiled.Add(new IndexEntry(
                e.Id,
                MapBotType(e.Categories),
                DisplayName(e.Id),
                accepted,
                CompilePatterns(e.Pattern.Forbidden)));
        }
        _entries = compiled.ToArray();
        // Invalidate the scan cache so a catalog update is seen immediately.
        _scanCache.Clear();
    }

    /// <summary>
    ///     Returns a match for the first arcjet entry whose accepted patterns
    ///     match <paramref name="userAgent"/> and none of whose forbidden patterns match.
    ///     Returns null when no entry matches or the index is empty (not yet loaded).
    ///     Results are cached per UA string via <see cref="BoundedCache{TKey,TValue}"/>
    ///     so repeat UAs hit the cache instead of re-scanning all 635 regexes.
    /// </summary>
    public WellKnownBotMatch? TryMatch(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var entries = _entries;
        if (entries.Length == 0) return null;

        // Cache hit: BoundedCache.TryGet also increments the LFU hit count so
        // popular human UAs (Chrome, Safari) stay resident while cold bot UAs
        // that appear once and never return are evicted first.
        if (_scanCache.TryGet(userAgent, out var cached))
            return cached;

        WellKnownBotMatch? result = null;
        foreach (var e in entries)
        {
            if (!MatchesAny(e.Accepted, userAgent)) continue;
            if (MatchesAny(e.Forbidden, userAgent)) continue;
            result = new WellKnownBotMatch(e.Id, e.BotType, e.DisplayName);
            break;
        }

        // Cache positive matches and confirmed misses (null) alike.
        // BoundedCache handles LFU eviction and TTL expiry — no manual trim.
        _scanCache.Set(userAgent, result);
        return result;
    }

    private static bool MatchesAny(Regex[] patterns, string ua)
    {
        foreach (var re in patterns)
        {
            try { if (re.IsMatch(ua)) return true; }
            catch (RegexMatchTimeoutException) { /* skip timed-out pattern */ }
        }
        return false;
    }

    private static Regex[] CompilePatterns(string[] patterns)
    {
        var result = new List<Regex>(patterns.Length);
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                result.Add(new Regex(p,
                    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    RegexTimeout));
            }
            catch (ArgumentException) { /* skip malformed patterns */ }
        }
        return result.ToArray();
    }

    private static string DisplayName(string id)
    {
        // "google-crawler" → "Google Crawler"
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
        Regex[] Accepted,
        Regex[] Forbidden);
}

/// <summary>Match result from <see cref="WellKnownBotIndex.TryMatch"/>.</summary>
public sealed record WellKnownBotMatch(string Id, BotType BotType, string DisplayName);