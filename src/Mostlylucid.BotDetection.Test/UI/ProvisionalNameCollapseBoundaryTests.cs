using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     The W3 boundary, pinned as a PROVEN BOUNDARY rather than an assumption (overview-
///     2026-09-09, after an independent measurement in a separate worktree).
///     <para>
///         The property: two distinct fingerprints whose stored name is the fallback
///         "Unclassified", with the same behavioural role and country, must NOT collapse into one
///         row — the always-present fp8 discriminator keeps their projected names distinct, and
///         the collapse key is the name.
///     </para>
///     <para>
///         The boundary: the discriminator is the first EIGHT characters of the signature. A
///         signature shorter than that projects without one, so two such rows produce the SAME
///         name and the group key folds them. Real signatures are 22-char base64url, so this is an
///         edge, not a live defect. The second test asserts the collapse HAPPENS and names it.
///         <b>Do not "fix" the boundary without a ruling</b>: removing it would change grouping
///         behaviour, which is a design change, not a bug fix.
///     </para>
/// </summary>
public sealed class ProvisionalNameCollapseBoundaryTests
{
    // Real signature shape: 22-char base64url, as SignatureAtom computes.
    private const string RealA = "6TyG2z5IQguu37X-O3c2xw";
    private const string RealB = "7KpQ2w9xABCDEFGHIJKLMN";

    private static SignatureAggregateCache NewCache() => new(new StyloBotDashboardOptions());

    private static DashboardDetectionEvent Detection(string signature) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Timestamp = DateTime.UtcNow,
        IsBot = true,
        BotProbability = 0.95,
        BotName = "Unclassified", // the persisted fallback the projection replaces
        BotType = "Scraper",
        CountryCode = "GB",
        PrimarySignature = signature,
        RiskBand = "VeryHigh",
        Confidence = 0.9,
        Method = "GET",
        Path = "/",
    };

    private static void Seed(SignatureAggregateCache cache, string signature)
    {
        cache.UpdateFromDetection(Detection(signature));
        cache.ApplyResolvedNames(new Dictionary<string, string?> { [signature] = "Unclassified" });
        cache.ApplyResolvedVerdicts(new Dictionary<string, ResolvedVerdict>
        {
            [signature] = new ResolvedVerdict(
                BotProbability: 0.95,
                RiskBand: "VeryHigh",
                BotType: "Scraper",
                Confidence: 0.9,
                ThreatScore: null,
                ThreatBand: null,
                IsBot: true,
                IsVerifiedBot: false),
        });
    }

    [Fact]
    public void Real_shaped_signatures_project_distinct_names_and_do_not_collapse()
    {
        var cache = NewCache();
        Seed(cache, RealA);
        Seed(cache, RealB);

        var (items, total, _, _) = cache.GetFiltered("bots", "lastSeen", "desc", page: 1, pageSize: 50);

        Assert.Equal(2, total);
        Assert.Contains(items, v => v.BotName == "Scraper 6TyG2z5I · GB");
        Assert.Contains(items, v => v.BotName == "Scraper 7KpQ2w9x · GB");
        Assert.DoesNotContain(items, v => v.BotName is not null && v.BotName.Contains("Unclassified"));
    }

    [Fact]
    public void Short_signatures_cannot_discriminate_so_they_collapse_the_known_boundary()
    {
        // KNOWN BOUNDARY, asserted deliberately (see the class doc). "sig-a"/"sig-b" are shorter
        // than the 8-char discriminator, so both rows project to the same role+country name and
        // the collapse key folds them into one row. Real signatures cannot hit this.
        var cache = NewCache();
        Seed(cache, "sig-a");
        Seed(cache, "sig-b");

        var (items, total, _, _) = cache.GetFiltered("bots", "lastSeen", "desc", page: 1, pageSize: 50);

        Assert.Equal(1, total);
        Assert.Equal("Scraper · GB", items[0].BotName);
    }
}
