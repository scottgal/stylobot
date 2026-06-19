using System;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins <see cref="WidgetRenderHelpers.CollapseGroupableIdentities"/>
///     centroid-by-identity grouping. ONE row per bot identity regardless of
///     whether earlier broadcasts persisted mixed casings ("Googlebot" /
///     "googlebot"), and rows are kept separate when they should be
///     (Spoofed-Googlebot vs Googlebot, Googlebot/2.0 vs Googlebot/2.1,
///     verified Googlebot vs unverified Googlebot).
/// </summary>
public class WidgetRenderHelpersCollapseTests
{
    private static DashboardTopBotEntry Row(
        string sig,
        string? botName,
        bool isVerified = false,
        string? userAgent = null,
        int hitCount = 1,
        double botProbability = 0.95,
        DateTime? lastSeen = null) => new()
    {
        PrimarySignature = sig,
        BotName = botName,
        IsVerifiedBot = isVerified,
        UserAgent = userAgent,
        HitCount = hitCount,
        BotProbability = botProbability,
        LastSeen = lastSeen ?? DateTime.UtcNow,
    };

    [Fact]
    public void Mixed_casing_googlebot_rows_collapse_to_one()
    {
        // Pre-canonicaliser data in the live event store: three rows that are
        // the same identity but stored with different cases. The centroid rule
        // (case-insensitive name grouping) folds them.
        var rows = new[]
        {
            Row("sig-a", "Googlebot", userAgent: "Googlebot/2.1", hitCount: 10),
            Row("sig-b", "googlebot", userAgent: "Googlebot/2.1", hitCount: 5),
            Row("sig-c", "GOOGLEBOT", userAgent: "Googlebot/2.1", hitCount: 2),
        };

        var result = WidgetRenderHelpers.CollapseGroupableIdentities(rows);

        Assert.Single(result);
        Assert.Equal(17, result[0].HitCount);  // sum across members
    }

    [Fact]
    public void Spoofed_prefix_does_not_collapse_with_unprefixed_identity()
    {
        // Spoofed- prefix marks a hostile identity; it must read as a separate
        // row from the legit verified bot. Spoof-protection per the user's
        // "compare UA + verification" rule.
        var rows = new[]
        {
            Row("sig-real",   "Googlebot",          isVerified: true,  userAgent: "Googlebot/2.1"),
            Row("sig-spoof",  "Spoofed-Googlebot",  isVerified: false, userAgent: "Googlebot/2.1"),
        };

        var result = WidgetRenderHelpers.CollapseGroupableIdentities(rows);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Different_major_ua_versions_stay_separate()
    {
        // A v1 crawler and a v2 crawler are different deployments and must
        // not fold into one row. Minor versions WITHIN a major (2.0 vs 2.1)
        // intentionally collapse -- they're the same crawler with rolling
        // sub-version churn, splitting them would over-fragment the centroid.
        var rows = new[]
        {
            Row("sig-v2", "Examplebot", userAgent: "Mozilla/5.0 (compatible; Examplebot/2.1)"),
            Row("sig-v1", "Examplebot", userAgent: "Mozilla/5.0 (compatible; Examplebot/1.0)"),
        };

        var result = WidgetRenderHelpers.CollapseGroupableIdentities(rows);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Minor_ua_versions_within_a_major_collapse()
    {
        // Document the intended sub-major folding: Googlebot/2.0 and 2.1 are
        // the same crawler with rolling minor churn. One row.
        var rows = new[]
        {
            Row("sig-2-1", "Googlebot", userAgent: "Mozilla/5.0 (compatible; Googlebot/2.1)"),
            Row("sig-2-0", "Googlebot", userAgent: "Mozilla/5.0 (compatible; Googlebot/2.0)"),
        };

        var result = WidgetRenderHelpers.CollapseGroupableIdentities(rows);

        Assert.Single(result);
    }

    [Fact]
    public void Verified_and_unverified_same_name_stay_separate()
    {
        // A verified Googlebot (FCrDNS OK) and an unverified Googlebot (rDNS
        // missing) are distinct actors and must not fold into one row.
        var rows = new[]
        {
            Row("sig-v", "Googlebot", isVerified: true,  userAgent: "Googlebot/2.1"),
            Row("sig-u", "Googlebot", isVerified: false, userAgent: "Googlebot/2.1"),
        };

        var result = WidgetRenderHelpers.CollapseGroupableIdentities(rows);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Empty_botname_rows_are_passthrough_not_grouped()
    {
        // Un-named rows have no centroid identity -- never fold them. Each
        // such row must round-trip as itself.
        var rows = new[]
        {
            Row("sig-a", botName: null, userAgent: "Mozilla/5.0", hitCount: 3),
            Row("sig-b", botName: null, userAgent: "Mozilla/5.0", hitCount: 4),
        };

        var result = WidgetRenderHelpers.CollapseGroupableIdentities(rows);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Collapsed_row_takes_max_probability_not_latest()
    {
        // A confirmed-name fingerprint that ever scored 1.0 must stay at 1.0
        // even if a subsequent low-score request gets folded in. Sticky-max,
        // mirroring SignatureAggregate's identity-aware probability rule.
        var rows = new[]
        {
            Row("sig-a", "Googlebot", userAgent: "Googlebot/2.1", botProbability: 1.0,  lastSeen: DateTime.UtcNow.AddMinutes(-5)),
            Row("sig-b", "Googlebot", userAgent: "Googlebot/2.1", botProbability: 0.25, lastSeen: DateTime.UtcNow),
        };

        var result = WidgetRenderHelpers.CollapseGroupableIdentities(rows);

        Assert.Single(result);
        Assert.Equal(1.0, result[0].BotProbability);
    }
}