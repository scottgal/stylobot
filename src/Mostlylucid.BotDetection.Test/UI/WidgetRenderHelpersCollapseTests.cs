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
///     (Spoofed-Googlebot vs Googlebot, verified Googlebot vs unverified
///     Googlebot). Grouping is by bot identity ONLY -- NEVER by the raw UA
///     string (operator rule 2026-08-12: "NO repeat names, ever"); the same
///     bot with different UA versions collapses to one row.
/// </summary>
public class WidgetRenderHelpersCollapseTests
{
    private static DashboardTopBotEntry Row(
        string sig,
        string? botName,
        bool isVerified = false,
        string? userAgent = null,
        string? botType = null,
        int hitCount = 1,
        double botProbability = 0.95,
        DateTime? lastSeen = null) => new()
    {
        PrimarySignature = sig,
        BotName = botName,
        IsVerifiedBot = isVerified,
        UserAgent = userAgent,
        BotType = botType,
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
    public void Different_ua_versions_of_the_same_bot_collapse_to_one()
    {
        // Operator rule 2026-08-12: "NO repeat names, ever" -- the same bot with
        // different UAs must collapse to ONE row with a clean name; grouping is
        // by bot identity, NEVER by the raw UA string. Previously the identity
        // key included the UA-extracted major version, which split PetalBot/2.0
        // vs PetalBot/3.0 into duplicate rows once rows carried a representative
        // UA (absorption aggregate rows after the 2026-08-12 signal fix). Both
        // major and minor version churn now fold into the single identity row.
        var rows = new[]
        {
            Row("sig-v2", "Examplebot", botType: "SearchEngine", userAgent: "Mozilla/5.0 (compatible; Examplebot/2.1)"),
            Row("sig-v1", "Examplebot", botType: "SearchEngine", userAgent: "Mozilla/5.0 (compatible; Examplebot/1.0)"),
            Row("sig-2-1", "Googlebot", userAgent: "Mozilla/5.0 (compatible; Googlebot/2.1)"),
            Row("sig-2-0", "Googlebot", userAgent: "Mozilla/5.0 (compatible; Googlebot/2.0)"),
        };

        var result = WidgetRenderHelpers.CollapseGroupableIdentities(rows);

        Assert.Equal(2, result.Count); // one Examplebot row + one Googlebot row
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
    public void ProjectAsVisitors_collapses_mixed_casing_rows_too()
    {
        // The /dashboard/visitors render path used to bypass collapse and showed
        // every fingerprint as its own row. ProjectAsVisitors must run the same
        // centroid collapse the Top Bots widget uses so the user-facing row
        // count agrees across surfaces.
        var rows = new[]
        {
            Row("sig-a", "Googlebot", userAgent: "Googlebot/2.1", hitCount: 4),
            Row("sig-b", "googlebot", userAgent: "Googlebot/2.1", hitCount: 3),
        };

        var (items, _, _) = WidgetRenderHelpers.ProjectAsVisitors(
            rows, filter: "all", sortField: "lastSeen", sortDir: "desc", page: 1, pageSize: 50);

        Assert.Single(items);
        Assert.Equal(7, items[0].Hits);
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