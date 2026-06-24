using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins the canonical name read path so the dashboard list, the signature
///     detail page, and the "Your Detection" card cannot disagree on the same
///     signature. The hard rule (user-stated 2026-06-24):
///     "ONE name at a time across all surfaces."
///     <para>
///         The LFU SignatureAggregateCache wins as long as it holds a non-fallback
///         name. A previously-persisted detection-row <c>bot_name</c> stays the
///         cold-render fallback (cache empty for this signature on first paint),
///         but it must NOT override a fresh name the matcher has already
///         recomposed into the cache — that was the pre-2026-06-24 priority that
///         caused list-vs-detail divergence ("list shows X, click through shows Y")
///         and rendered stale "Chrome Desktop" / "Unknown 000000" rows.
///     </para>
/// </summary>
public class ResolveBotNameCanonicalReadTests
{
    private static SignatureAggregateCache NewCache()
        => new(new StyloBotDashboardOptions());

    [Fact]
    public void Cache_hit_with_real_name_wins_over_stored_value()
    {
        var cache = NewCache();
        cache.ApplyResolvedNames(new Dictionary<string, string?> { ["sig-1"] = "Googlebot" });
        var lookup = new Dictionary<string, string?>();

        // The "stored" name here is the stale value persisted on the latest detection
        // row — the lying "Chrome Desktop" the user called out. Cache has the fresh
        // recomposed verdict-honest name. Cache must win.
        var resolved = lookup.ResolveBotName(cache, "sig-1", storedName: "Chrome Desktop");

        Assert.Equal("Googlebot", resolved);
    }

    [Fact]
    public void Stored_real_name_wins_over_cache_fallback()
    {
        var cache = NewCache();
        cache.ApplyResolvedNames(new Dictionary<string, string?> { ["sig-1"] = "Unknown" });
        var lookup = new Dictionary<string, string?>();

        // Cache has the fallback "Unknown"; the detection row carries a real
        // catalog name. The real catalog name must win — fallback in cache
        // doesn't outrank a previously-persisted real name.
        var resolved = lookup.ResolveBotName(cache, "sig-1", storedName: "Googlebot");

        Assert.Equal("Googlebot", resolved);
    }

    [Fact]
    public void Cache_fallback_wins_over_no_stored_value()
    {
        var cache = NewCache();
        cache.ApplyResolvedNames(new Dictionary<string, string?> { ["sig-1"] = "Unknown" });
        var lookup = new Dictionary<string, string?>();

        var resolved = lookup.ResolveBotName(cache, "sig-1", storedName: null);

        Assert.Equal("Unknown", resolved);
    }

    [Fact]
    public void Lookup_dict_falls_in_after_cache_and_stored_fallbacks()
    {
        var cache = NewCache();
        var lookup = new Dictionary<string, string?> { ["sig-1"] = "Bingbot" };

        // Cache cold for this signature; stored value is null; signatures-lookup
        // dict carries a real name -> wins.
        var resolved = lookup.ResolveBotName(cache, "sig-1", storedName: null);

        Assert.Equal("Bingbot", resolved);
    }

    [Fact]
    public void Stored_fallback_returned_when_everything_else_is_empty()
    {
        var cache = NewCache();
        var lookup = new Dictionary<string, string?>();

        // Last-resort: nothing fresh anywhere. Returning the stored fallback is
        // strictly better than null because the dashboard render layer would
        // otherwise fall through to the signature substring.
        var resolved = lookup.ResolveBotName(cache, "sig-1", storedName: "Unknown 00000000");

        Assert.Equal("Unknown 00000000", resolved);
    }

    [Fact]
    public void Empty_everywhere_returns_null()
    {
        var cache = NewCache();
        var lookup = new Dictionary<string, string?>();

        var resolved = lookup.ResolveBotName(cache, "sig-1", storedName: null);

        Assert.Null(resolved);
    }

    [Fact]
    public void Null_cache_is_tolerated_and_falls_through_to_stored_and_lookup()
    {
        var lookup = new Dictionary<string, string?> { ["sig-1"] = "Bingbot" };

        // Pure dashboard-viewer hosts don't register the cache. ResolveBotName
        // must accept that and degrade to stored/lookup.
        var resolved = lookup.ResolveBotName(cache: null, "sig-1", storedName: "Googlebot");

        Assert.Equal("Googlebot", resolved);
    }

    [Fact]
    public void Same_signature_resolves_to_same_name_for_list_and_detail_call_sites()
    {
        // This is the structural pin for the user's "ONE name at a time" rule.
        // The list (TopBots / Visitors) calls ResolveBotName with the visitor's
        // persisted BotName; the detail page now calls ResolveBotName with the
        // latest detection's BotName. Both go through the same cache lookup,
        // so the SAME signature MUST produce the SAME name regardless of which
        // side fed in the stored fallback.
        var cache = NewCache();
        cache.ApplyResolvedNames(new Dictionary<string, string?> { ["sig-1"] = "Mastodon mastodon.social" });
        var lookup = new Dictionary<string, string?> { ["sig-1"] = "ignored-because-cache-wins" };

        var fromList   = lookup.ResolveBotName(cache, "sig-1", storedName: "older list-side stale name");
        var fromDetail = lookup.ResolveBotName(cache, "sig-1", storedName: "newer detail-side stale name");

        Assert.Equal(fromList, fromDetail);
        Assert.Equal("Mastodon mastodon.social", fromList);
    }
}