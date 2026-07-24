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
    ///     Raw top-bots snapshot for the TopBots row's own fixed window: trailing 24h,
    ///     unfiltered audience, capped at <paramref name="maxEntries"/>. Mirrors the fetch
    ///     half of <c>StyloBotDashboardMiddleware.BuildTopBotsModel</c> verbatim.
    /// </summary>
    internal static async Task<IReadOnlyList<DashboardTopBotEntry>> FetchTopBotsRawAsync(
        IDashboardEventStore store, int maxEntries, CancellationToken ct = default)
    {
        var raw = await store.GetTopBotsAsync(
            count: maxEntries,
            startTime: DateTime.UtcNow.AddHours(-24),
            endTime: DateTime.UtcNow,
            audienceFilter: "all");
        return raw;
    }

    /// <summary>Raw threats snapshot (top-N, unpaginated) via <see cref="IDashboardEventStore.GetThreatsAsync"/>.</summary>
    internal static async Task<IReadOnlyList<ThreatEntry>> FetchThreatsRawAsync(
        IDashboardEventStore store, int count, CancellationToken ct = default)
        => await store.GetThreatsAsync(count);

    /// <summary>
    ///     Raw cluster snapshot + diagnostics via <see cref="IBotClusterReader"/>. Returns
    ///     an empty list and null diagnostics when no cluster reader is registered (FOSS
    ///     hosts without the clustering pack) -- mirrors the existing null-check in
    ///     <c>BuildClustersModelAsync</c>.
    /// </summary>
    internal static async Task<(IReadOnlyList<ClusterViewModel> Clusters, ClusterDiagnosticsViewModel? Diagnostics)> FetchClustersRawAsync(
        IBotClusterReader? clusterReader, CancellationToken ct = default)
    {
        if (clusterReader is null) return (Array.Empty<ClusterViewModel>(), null);

        var rawClusters = await clusterReader.GetClustersAsync(ct);
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
                AverageThreatScore = Math.Round(cl.AverageThreatScore, 3)
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
    /// </summary>
    internal static async Task<(IReadOnlyList<SessionListEntry> Entries, int TotalCount)> FetchSessionsRawAsync(
        IDetectionArchive? archive,
        IDashboardEventStore store,
        Services.SignatureAggregateCache signatureCache,
        TimeSpan retention,
        int page,
        int pageSize,
        string? filter,
        CancellationToken ct = default)
    {
        if (archive is null) return (Array.Empty<SessionListEntry>(), 0);

        bool? isBot = filter switch { "bot" => true, "human" => false, _ => null };
        var since = DateTime.UtcNow - retention;
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
}
