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
    // DELETED: GetFiltered_collapses_mixed_casing_bot_rows_to_one
    //
    // The test pinned the case-insensitive collapse driven by the per-detection
    // BotName write path. Per the 2026-06-19-single-source-fingerprint-name spec
    // the cache no longer accepts names from detection events (they were the
    // parasitic source of banned-shape names on the visitor list); names enter
    // only via SignatureAggregateCache.ApplyResolvedNames fed by the
    // contract-gated IFingerprintStore.GetDisplayNamesBySignaturesAsync. With
    // that write path severed, two UpdateFromDetection calls produce two
    // nameless rows -- the collapse case the test pinned cannot arise in the
    // production cache anymore. Case-insensitive identity-grouping coverage
    // belongs in the BehaviouralGrouper tests, which key off the
    // store-resolved name + group_key path.

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
