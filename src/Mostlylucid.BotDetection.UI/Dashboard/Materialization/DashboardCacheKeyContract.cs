using Mostlylucid.BotDetection.UI.Dashboard.Composition;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     Boot-time structural lock on cache keys (operator directive 2026-08-12): the revert
///     loop of "a read path derives an envelope the prewarm never warms → permanent cold
///     miss → silent zeros" must end. This verifies, before serving traffic, that every
///     top-level page's DEFAULT read window resolves — through the single
///     <see cref="DashboardRoutingHelpers.BuildPinnedWindow"/> derivation — to an envelope the
///     pinned prewarm covers. Any mismatch throws at boot: fail loud, never serve silent
///     zeros.
///     <para>
///         Read paths verified: TrafficController's default (the layout's
///         DefaultTimeWindowMinutes token), VisitorsController's fixed 24h, and
///         SiteController's 24h default — the three read paths that have actually drifted
///         against the prewarm (bucket 24-vs-20, bucket 60-vs-20). Custom ranges and
///         explicit ?window= tokens outside the pinned set remain demand-warmed by design.
///     </para>
/// </summary>
public static class DashboardCacheKeyContract
{
    /// <summary>
    ///     Verifies the prewarm/read envelope-key contract. Throws
    ///     <see cref="InvalidOperationException"/> naming the offending read path on any
    ///     violation — the coordinator calls this at boot (StartAsync), so a violation fails
    ///     host startup instead of serving cold reads.
    /// </summary>
    /// <param name="options">The materializer options (pinned page keys + window tokens).</param>
    /// <param name="manifests">The seeded manifest source (same instance every read path uses).</param>
    /// <param name="defaultWindowMinutes">
    ///     The layout's DefaultTimeWindowMinutes — the window a request with no explicit
    ///     ?window= resolves to on the traffic page.
    /// </param>
    /// <param name="now">A single clock sample so every envelope floors to the same bucket.</param>
    public static void VerifyPrewarmCoverage(
        DashboardMaterializerOptions options,
        IDashboardPageManifestSource manifests,
        int defaultWindowMinutes,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifests);

        var pinnedKeys = options.PrewarmPageKeys.Count > 0
            ? options.PrewarmPageKeys
            : new[] { options.PrewarmPageKey };
        var pinnedTokens = options.PrewarmWindows;

        // The prewarm's own envelope set — every pinned manifest × token.
        var prewarmed = new HashSet<DashboardContentEnvelope>();
        foreach (var key in pinnedKeys)
        {
            if (manifests.For(key) is not { } manifest) continue;
            foreach (var token in pinnedTokens)
                prewarmed.Add(DashboardContentEnvelope.From(
                    manifest, DashboardRoutingHelpers.BuildPinnedWindow(token, now)));
        }

        var traffic = manifests.For("dashboard.traffic")
                      ?? throw new InvalidOperationException(
                          "Dashboard cache-key contract: the dashboard.traffic manifest is missing; the traffic read path has no envelope to resolve.");

        // 1. The traffic page's DEFAULT view (no ?window=) — the layout default token.
        var defaultToken = DashboardRoutingHelpers.WindowTokenForMinutes(defaultWindowMinutes);
        AssertCovered(prewarmed, traffic, defaultToken, $"TrafficController default ({defaultToken})", now);

        // 2. The visitors page — always reads the traffic manifest at a fixed 24h window.
        AssertCovered(prewarmed, traffic, "24h", "VisitorsController fixed 24h", now);

        // 3. The site page's default view.
        if (manifests.For("dashboard.site") is { } site)
            AssertCovered(prewarmed, site, "24h", "SiteController default (24h)", now);
    }

    private static void AssertCovered(
        HashSet<DashboardContentEnvelope> prewarmed,
        DashboardPageManifest manifest,
        string token,
        string readPath,
        DateTime now)
    {
        var readEnvelope = DashboardContentEnvelope.From(
            manifest, DashboardRoutingHelpers.BuildPinnedWindow(token, now));
        if (prewarmed.Contains(readEnvelope)) return;

        throw new InvalidOperationException(
            $"Dashboard cache-key contract violated at boot: the {readPath} read path resolves window " +
            $"'{token}' to an envelope the pinned prewarm does not cover. Add the token to " +
            $"PrewarmWindows (or the page to PrewarmPageKeys) or fix the read derivation — a " +
            "permanent cold miss must never ship silently.");
    }
}
