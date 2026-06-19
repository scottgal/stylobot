using System;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins case-insensitive collapse in <see cref="SignatureAggregateCache"/>.
///     Two fingerprints that resolved to the same bot identity but landed in
///     the cache with different name casings ("Googlebot" / "googlebot") --
///     usually stale rows from before the upstream canonicaliser shipped --
///     must fold to ONE row when the cache returns rows for the Visitors,
///     Sessions, or Threats surfaces.
/// </summary>
public class SignatureAggregateCacheCaseInsensitiveCollapseTests
{
    [Fact]
    public void GetFiltered_collapses_mixed_casing_bot_rows_to_one()
    {
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());

        // Two distinct fingerprints, same identity, two different casings -- the
        // exact shape we hit on staging when a contributor wrote the lowercase
        // variant on one request and the catalog wrote the canonical on another.
        cache.UpdateFromDetection(MakeDetection("sig-canonical", "Googlebot", DateTime.UtcNow.AddSeconds(-30)));
        cache.UpdateFromDetection(MakeDetection("sig-lower",     "googlebot", DateTime.UtcNow));

        var (items, totalCount, _, _) =
            cache.GetFiltered("bots", "lastSeen", "desc", page: 1, pageSize: 50);

        Assert.Equal(1, totalCount);
        Assert.Single(items);
    }

    [Fact]
    public void GetFiltered_keeps_distinct_identities_separate()
    {
        // Different bot identities ("Googlebot" vs "Bingbot") must remain
        // distinct -- the case-insensitive fold groups same-name rows only.
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());

        cache.UpdateFromDetection(MakeDetection("sig-g", "Googlebot", DateTime.UtcNow.AddSeconds(-30)));
        cache.UpdateFromDetection(MakeDetection("sig-b", "bingbot",   DateTime.UtcNow));

        var (items, totalCount, _, _) =
            cache.GetFiltered("bots", "lastSeen", "desc", page: 1, pageSize: 50);

        Assert.Equal(2, totalCount);
        Assert.Equal(2, items.Count);
    }

    private static DashboardDetectionEvent MakeDetection(string primarySignature, string botName, DateTime ts) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Timestamp = ts,
        IsBot = true,
        BotProbability = 0.95,
        BotName = botName,
        BotType = "SearchEngine",
        PrimarySignature = primarySignature,
        RiskBand = "High",
        Confidence = 0.9,
        Method = "GET",
        Path = "/",
    };
}
