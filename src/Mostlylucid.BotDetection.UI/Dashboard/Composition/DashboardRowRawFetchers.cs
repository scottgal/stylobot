using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     Stage 2a: the raw (unshaped) fetch logic for the Clusters/TopBots/Sessions/Threats
///     dashboard rows, extracted so BOTH <see cref="StyloBotDashboardMiddleware"/>'s
///     per-request Build* methods (unchanged, request-thread friendly for the HTMX
///     partial-refresh endpoints -- out of Stage 2a's scope) AND
///     <see cref="DefaultDashboardPageComposer"/> (the out-of-request, tick-driven warm
///     path) can produce the identical dataset from one place.
///
///     <para>
///         These four rows aren't <c>IDashboardEventStore.ComposeBatchAsync</c>/<see
///         cref="Models.DatasetKind"/> candidates: Clusters comes from
///         <see cref="IBotClusterReader"/>, Sessions from <see cref="IDetectionArchive"/>,
///         and TopBots/Threats use fixed windows distinct from the Traffic page's own
///         (see <see cref="DashboardPageResult.TopBotsRaw"/>). Extracting the fetch (not
///         the shape/paginate/sort step, which stays cheap and request-thread-safe) is
///         what lets the tick materializer warm them ahead of a read.
///     </para>
/// </summary>
internal static class DashboardRowRawFetchers
{
    /// <summary>
    ///     Fallback retention when no <c>StyloBotDashboardOptions</c> is available to the
    ///     caller (mirrors <c>StyloBotDashboardOptions.DetectionRetention</c>'s own default;
    ///     production always passes the real configured value via
    ///     <see cref="DefaultDashboardPageComposer"/>'s DI-resolved options).
    /// </summary>
    internal static readonly TimeSpan DefaultDetectionRetention = TimeSpan.FromDays(30);

    /// <summary>
    ///     Raw top-bots snapshot for the TopBots row, scoped to <paramref name="startTime"/>/
    ///     <paramref name="endTime"/> -- the dashboard's currently-selected <c>?window=</c>
    ///     period (see <c>StyloBotDashboardMiddleware.BuildVisitorsPageWindow</c>), capped at
    ///     <paramref name="maxEntries"/>. When both are null (no manifest window resolved --
    ///     e.g. a caller that never threads one through), falls back to the historical fixed
    ///     trailing-24h lookback so callers that don't supply a window keep the pre-existing
    ///     behavior.
    /// </summary>
    internal static async Task<IReadOnlyList<DashboardTopBotEntry>> FetchTopBotsRawAsync(
        IDashboardEventStore store, int maxEntries,
        DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default)
    {
        var raw = await store.GetTopBotsAsync(
            count: maxEntries,
            startTime: startTime ?? DateTime.UtcNow.AddHours(-24),
            endTime: endTime ?? DateTime.UtcNow,
            audienceFilter: "all");
        return raw;
    }

    /// <summary>
    ///     Raw threats snapshot (top-N, unpaginated) via <see cref="IDashboardEventStore.GetThreatsAsync"/>,
    ///     scoped to <paramref name="startTime"/>/<paramref name="endTime"/> -- the dashboard's
    ///     currently-selected <c>?window=</c> period. Null bounds fall through to the store's
    ///     own default (all-time), matching the store's pre-existing null-means-unbounded contract.
    /// </summary>
    internal static async Task<IReadOnlyList<ThreatEntry>> FetchThreatsRawAsync(
        IDashboardEventStore store, int count,
        DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default)
        => await store.GetThreatsAsync(count, startTime, endTime);

    /// <summary>
    ///     Raw cluster snapshot + diagnostics via <see cref="IBotClusterReader"/>. Returns
    ///     an empty list and null diagnostics when no cluster reader is registered (FOSS
    ///     hosts without the clustering pack) -- mirrors the existing null-check in
    ///     <c>BuildClustersModelAsync</c>.
    ///
    ///     <para>
    ///         Operator ruling: cluster MEMBERSHIP (which signatures belong to which cluster)
    ///         stays CURRENT-STATE -- Leiden is a relatively expensive periodic background
    ///         computation (<see cref="BotClusterService"/>) and is deliberately NOT re-run
    ///         per dashboard window; re-running it per window would also change what a
    ///         "cluster" even means moment to moment. Only the per-cluster ACTIVITY metric
    ///         (<see cref="ClusterViewModel.WindowHitCount"/>) is scoped to
    ///         <paramref name="startTime"/>/<paramref name="endTime"/>, exactly like every
    ///         other row. This reuses the SAME windowed store call the TopBots row already
    ///         makes (<see cref="IDashboardEventStore.GetTopBotsAsync"/>) rather than adding a
    ///         new store method: one windowed top-bots snapshot (capped at
    ///         <paramref name="activityLookupCap"/>, same cap TopBots itself uses), joined onto
    ///         each cluster's member signatures. A member signature outside that cap reads as
    ///         zero windowed hits -- the same precision ceiling the TopBots row itself has, and
    ///         a deliberate reuse of existing infra over a new IDashboardEventStore surface
    ///         (see the design-decision note in the PR description).
    ///     </para>
    /// </summary>
    internal static async Task<(IReadOnlyList<ClusterViewModel> Clusters, ClusterDiagnosticsViewModel? Diagnostics)> FetchClustersRawAsync(
        IBotClusterReader? clusterReader, IDashboardEventStore store,
        DateTime? startTime, DateTime? endTime, int activityLookupCap, CancellationToken ct = default)
    {
        if (clusterReader is null) return (Array.Empty<ClusterViewModel>(), null);

        var rawClusters = await clusterReader.GetClustersAsync(ct);

        // Skip the windowed activity lookup entirely when there are no clusters to
        // annotate -- avoids a wasted store round-trip on an empty snapshot.
        Dictionary<string, int> hitsBySignature;
        if (rawClusters.Count == 0)
        {
            hitsBySignature = new Dictionary<string, int>(StringComparer.Ordinal);
        }
        else
        {
            var windowedHits = await store.GetTopBotsAsync(
                count: activityLookupCap, startTime: startTime, endTime: endTime, audienceFilter: "all");
            hitsBySignature = windowedHits
                .GroupBy(h => h.PrimarySignature, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(h => h.HitCount), StringComparer.Ordinal);
        }

        var clusters = rawClusters
            .Select(cl => new ClusterViewModel
            {
                ClusterId = cl.ClusterId,
                Label = cl.Label ?? "Unknown",
                Description = cl.Description,
                Type = cl.Type.ToString(),
                MemberCount = cl.MemberCount,
                AvgBotProb = Math.Round(cl.AverageBotProbability, 3),
                Country = cl.DominantCountry,
                AverageSimilarity = Math.Round(cl.AverageSimilarity, 3),
                TemporalDensity = Math.Round(cl.TemporalDensity, 3),
                DominantIntent = cl.DominantIntent,
                AverageThreatScore = Math.Round(cl.AverageThreatScore, 3),
                WindowHitCount = cl.MemberSignatures.Sum(sig =>
                    hitsBySignature.TryGetValue(sig, out var h) ? h : 0)
            })
            .ToList();

        var diagnosticsSnapshot = await clusterReader.GetDiagnosticsAsync(ct);
        var diagnostics = new ClusterDiagnosticsViewModel
        {
            Algorithm = diagnosticsSnapshot.Algorithm,
            Status = diagnosticsSnapshot.Status,
            LastRunAt = diagnosticsSnapshot.LastRunAt,
            InputBehaviorCount = diagnosticsSnapshot.InputBehaviorCount,
            EdgeCount = diagnosticsSnapshot.EdgeCount,
            GraphDensity = Math.Round(diagnosticsSnapshot.GraphDensity, 3),
            RawCommunityCount = diagnosticsSnapshot.RawCommunityCount,
            ClusterCount = diagnosticsSnapshot.ClusterCount,
            HumanClusterCount = diagnosticsSnapshot.HumanCount,
            MachineClusterCount = diagnosticsSnapshot.ProductCount + diagnosticsSnapshot.NetworkCount + diagnosticsSnapshot.EmergentCount,
            MixedClusterCount = diagnosticsSnapshot.MixedCount,
            SimilarityThreshold = diagnosticsSnapshot.SimilarityThreshold,
            MinClusterSize = diagnosticsSnapshot.MinClusterSize,
            TopWeights = diagnosticsSnapshot.TopWeights
                .OrderByDescending(w => w.Value)
                .Take(6)
                .ToList()
        };

        return (clusters, diagnostics);
    }

    /// <summary>
    ///     Raw recent-sessions snapshot via <see cref="IDetectionArchive"/>, enriched with
    ///     bot-name/user-agent lookups. Mirrors the fetch half of
    ///     <c>StyloBotDashboardMiddleware.BuildSessionsModel</c> verbatim, including its
    ///     fetch-count cap (<paramref name="page"/>/<paramref name="pageSize"/> only affect
    ///     how MANY rows are fetched up-front -- shaping/pagination of the fetched set still
    ///     happens in the caller). Returns an empty result when no archive is registered.
    ///
    ///     <para>
    ///         <paramref name="startTime"/>, when supplied, is the dashboard's currently-
    ///         selected <c>?window=</c> lower bound and takes priority over
    ///         <paramref name="retention"/> for the "since" cutoff -- <paramref name="retention"/>
    ///         remains the fallback for callers that don't have a resolved window (matches the
    ///         pre-existing <c>StyloBotDashboardOptions.DetectionRetention</c>-derived behavior).
    ///         <see cref="IDetectionArchive.GetRecentSessionsAsync"/> has no upper-bound
    ///         parameter; the window's end is always "now" for every currently-supported window
    ///         token (6h/24h/7d/30d), so an explicit upper bound isn't needed for correctness.
    ///     </para>
    /// </summary>
    internal static async Task<(IReadOnlyList<SessionListEntry> Entries, int TotalCount)> FetchSessionsRawAsync(
        IDetectionArchive? archive,
        IDashboardEventStore store,
        Services.SignatureAggregateCache signatureCache,
        TimeSpan retention,
        int page,
        int pageSize,
        string? filter,
        DateTime? startTime = null,
        CancellationToken ct = default)
    {
        if (archive is null) return (Array.Empty<SessionListEntry>(), 0);

        bool? isBot = filter switch { "bot" => true, "human" => false, _ => null };
        var since = startTime ?? (DateTime.UtcNow - retention);
        const int maxFetch = 500;
        var fetchCount = Math.Min(page * pageSize + pageSize, maxFetch);
        var sessions = await archive.GetRecentSessionsAsync(fetchCount, isBot, since, ct);
        var totalCount = sessions.Count < maxFetch ? sessions.Count : maxFetch;

        var sigLookup = await store.LoadSignatureLookupAsync();
        var uaLookup = await store.LoadUserAgentLookupAsync();

        var entries = sessions.Select(s => new SessionListEntry
        {
            Id = s.Id,
            Signature = s.Signature,
            StartedAt = s.StartedAt,
            EndedAt = s.EndedAt,
            RequestCount = s.RequestCount,
            DominantState = s.DominantState,
            IsBot = s.IsBot,
            AvgBotProbability = s.AvgBotProbability,
            RiskBand = s.RiskBand,
            Action = s.Action,
            BotName = sigLookup.ResolveBotName(signatureCache, s.Signature, s.BotName),
            CountryCode = s.CountryCode,
            UserAgent = uaLookup.ResolveUserAgent(signatureCache, s.Signature),
            ErrorCount = s.ErrorCount,
            TimingEntropy = s.TimingEntropy,
            Maturity = s.Maturity,
            TransitionCounts = s.TransitionCountsJson != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(s.TransitionCountsJson)
                : null
        }).ToList();

        return (entries, totalCount);
    }

    /// <summary>
    ///     Raw user-agent family summary via <see cref="Services.DashboardUserAgentAggregator"/>,
    ///     scoped to <paramref name="startTime"/>/<paramref name="endTime"/> -- the dashboard's
    ///     currently-selected <c>?window=</c> period. Reuses the SAME windowed aggregation
    ///     service <see cref="Mostlylucid.BotDetection.UI.ViewComponents.Dashboard.SbUserAgentsListViewComponent"/>
    ///     already calls when a range/audience is explicitly requested -- this just threads the
    ///     dashboard's page window through it instead of the fixed <c>DashboardAggregateCache</c>
    ///     snapshot the Stage-2a shell-model field previously read.
    /// </summary>
    internal static async Task<IReadOnlyList<Models.DashboardUserAgentSummary>> FetchUserAgentsRawAsync(
        Services.DashboardUserAgentAggregator aggregator,
        DateTime? startTime, DateTime? endTime, CancellationToken ct = default)
        => await aggregator.ComputeAsync(audienceFilter: "all", startTime, endTime);
}
