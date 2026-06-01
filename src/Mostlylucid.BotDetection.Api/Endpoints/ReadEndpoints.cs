using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Risk;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

// Common-case (default-args) responses are served from
// DashboardAggregateCache, which DashboardSummaryBroadcaster refreshes every
// SummaryBroadcastIntervalSeconds. Filtered / windowed / paginated queries
// (custom since/until/offset/etc.) fall through to the store unchanged --
// the cache is a fast-path, not a gate.

public static class ReadEndpoints
{
    public static IEndpointRouteBuilder MapReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Dashboard Data")
            .WithApiBotPolicy();

        group.MapGet("/detections", HandleDetections).WithName("GetDetections");
        group.MapGet("/signatures", HandleSignatures).WithName("GetSignatures");
        group.MapGet("/summary", HandleSummary).WithName("GetSummary");
        group.MapGet("/timeseries", HandleTimeseries).WithName("GetTimeseries");
        group.MapGet("/countries", HandleCountries).WithName("GetCountries");
        group.MapGet("/countries/{code}", HandleCountryDetail).WithName("GetCountryDetail");
        group.MapGet("/endpoints", HandleEndpoints).WithName("GetEndpoints");
        group.MapGet("/endpoints/{method}/{**path}", HandleEndpointDetail).WithName("GetEndpointDetail");
        group.MapGet("/topbots", HandleTopBots).WithName("GetTopBots");
        group.MapGet("/threats", HandleThreats).WithName("GetThreats");

        return endpoints;
    }

    private static async Task<Ok<PaginatedResponse<DashboardDetectionEvent>>> HandleDetections(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        [FromServices] SignatureAggregateCache signatureCache,
        int limit = 50, int offset = 0, bool? isBot = null, DateTime? since = null, string? signature = null)
    {
        aggregateCache.MarkHit();
        var cappedLimit = Math.Min(limit, 200);
        var snapshot = aggregateCache.Current;
        // The precomputed snapshot holds the most recent detections across ALL
        // signatures, so it can only short-circuit the unfiltered default view.
        // A signature-scoped query (the signature-detail page) must hit the store.
        if (offset == 0 && isBot is null && since is null && string.IsNullOrEmpty(signature)
            && snapshot.Detections.Count >= cappedLimit
            && snapshot.ComputedAt != DateTime.MinValue)
        {
            var slice = snapshot.Detections.Take(cappedLimit).ToList();
            return TypedResults.Ok(new PaginatedResponse<DashboardDetectionEvent>
            {
                Data = slice,
                Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = slice.Count },
                Meta = new ResponseMeta()
            });
        }

        // Signature-scoped, latest-only query (limit=1): the "You: Bot/Human X%" pill
        // path. DetectionBroadcastMiddleware updates the in-memory SignatureAggregateCache
        // SYNCHRONOUSLY before the request proxies downstream (per
        // [[feedback_write_behind_lfu_facade]]: dict is truth, DB is durability), so this
        // cache read sees the CURRENT request's verdict -- the DB row is still being
        // written behind by the fire-and-forget drainer. Synthesise a one-row
        // DashboardDetectionEvent from the aggregate so the remote caller (the dashboard
        // host's BuildYourDetectionPartialModel) gets a single source of truth for the
        // headline fields without a parallel header / second source.
        if (!string.IsNullOrEmpty(signature) && cappedLimit == 1 && offset == 0
            && isBot is null && since is null)
        {
            var agg = signatureCache.TryGet(signature, out var a) ? a : null;
            if (agg is not null)
            {
                var det = SynthesizeDetectionFromAggregate(signature, agg);
                return TypedResults.Ok(new PaginatedResponse<DashboardDetectionEvent>
                {
                    Data = new List<DashboardDetectionEvent> { det },
                    Pagination = new PaginationInfo { Offset = 0, Limit = 1, Total = 1 },
                    Meta = new ResponseMeta()
                });
            }
            // Cache miss falls through to store -- cold sig (evicted or never warmed).
        }

        var filter = new DashboardFilter
        {
            Limit = cappedLimit, Offset = offset, IsBot = isBot, StartTime = since,
            SignatureId = string.IsNullOrEmpty(signature) ? null : signature
        };
        var detections = await store.GetDetectionsAsync(filter);
        return TypedResults.Ok(new PaginatedResponse<DashboardDetectionEvent>
        {
            Data = detections,
            Pagination = new PaginationInfo { Offset = offset, Limit = limit, Total = detections.Count },
            Meta = new ResponseMeta()
        });
    }

    /// <summary>
    ///     Build a DashboardDetectionEvent from a SignatureAggregate so the cache-fast-path
    ///     in HandleDetections can return one shape for one request without going to SQL.
    ///     The aggregate is the rolling per-signature view (updated synchronously by
    ///     DetectionBroadcastMiddleware on every detection); the synthesised event reflects
    ///     the LATEST verdict for the signature. Per-request-only fields (RequestId, Path,
    ///     Method, StatusCode, etc.) are stamped to placeholder/zero values since the cache
    ///     doesn't track per-request rows.
    /// </summary>
    private static DashboardDetectionEvent SynthesizeDetectionFromAggregate(
        string signature, SignatureAggregate agg) => new()
    {
        RequestId = "cache",
        Timestamp = agg.LastSeen == default ? DateTime.UtcNow : agg.LastSeen,
        IsBot = agg.IsBot,
        BotProbability = agg.BotProbability,
        Confidence = agg.Confidence,
        RiskBand = agg.RiskBand ?? "Unknown",
        BotType = agg.BotType,
        BotName = agg.BotName,
        Action = agg.Action,
        Method = "GET",
        Path = "/",
        StatusCode = 0,
        ProcessingTimeMs = agg.ProcessingTimeMs,
        TopReasons = agg.TopReasons ?? new List<string>(),
        PrimarySignature = signature,
        EntityId = agg.EntityId,
        CountryCode = agg.CountryCode,
        Description = agg.Description,
        Narrative = agg.Narrative,
        ThreatScore = agg.ThreatScore,
        ThreatBand = agg.ThreatBand,
        RiskJustification = agg.RiskJustification,
        IsVerifiedBot = agg.IsVerifiedBot,
    };

    private static async Task<Ok<PaginatedResponse<DashboardSignatureEvent>>> HandleSignatures(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        int limit = 100, int offset = 0, bool? isBot = null)
    {
        aggregateCache.MarkHit();
        var signatures = await store.GetSignaturesAsync(limit, offset, isBot);
        return TypedResults.Ok(new PaginatedResponse<DashboardSignatureEvent>
        {
            Data = signatures,
            Pagination = new PaginationInfo { Offset = offset, Limit = limit, Total = signatures.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Ok<SingleResponse<DashboardSummary>>> HandleSummary(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache)
    {
        aggregateCache.MarkHit();
        var snapshot = aggregateCache.Current;
        if (snapshot.Summary is not null && snapshot.ComputedAt != DateTime.MinValue)
            return TypedResults.Ok(new SingleResponse<DashboardSummary>
            {
                Data = snapshot.Summary, Meta = new ResponseMeta()
            });

        var summary = await store.GetSummaryAsync();
        return TypedResults.Ok(new SingleResponse<DashboardSummary> { Data = summary, Meta = new ResponseMeta() });
    }

    private static async Task<Ok<PaginatedResponse<DashboardTimeSeriesPoint>>> HandleTimeseries(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        string interval = "5m", DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var snapshot = aggregateCache.Current;
        // Default-view window only: trailing 24 h at 5-minute buckets.
        // Anything else gets the store path.
        if (since is null && until is null && interval == "5m"
            && snapshot.TimeSeries.Count > 0
            && snapshot.ComputedAt != DateTime.MinValue)
        {
            return TypedResults.Ok(new PaginatedResponse<DashboardTimeSeriesPoint>
            {
                Data = snapshot.TimeSeries,
                Pagination = new PaginationInfo
                {
                    Offset = 0, Limit = snapshot.TimeSeries.Count, Total = snapshot.TimeSeries.Count
                },
                Meta = new ResponseMeta()
            });
        }

        var bucketSize = interval switch
        {
            "1m" => TimeSpan.FromMinutes(1), "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15), "1h" => TimeSpan.FromHours(1),
            _ => TimeSpan.FromMinutes(5)
        };
        var start = since ?? DateTime.UtcNow.AddHours(-24);
        var end = until ?? DateTime.UtcNow;
        var timeseries = await store.GetTimeSeriesAsync(start, end, bucketSize);
        return TypedResults.Ok(new PaginatedResponse<DashboardTimeSeriesPoint>
        {
            Data = timeseries,
            Pagination = new PaginationInfo { Offset = 0, Limit = timeseries.Count, Total = timeseries.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Ok<PaginatedResponse<DashboardCountryStats>>> HandleCountries(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        int limit = 20, DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var countries = await store.GetCountryStatsAsync(limit, since, until);
        return TypedResults.Ok(new PaginatedResponse<DashboardCountryStats>
        {
            Data = countries,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = countries.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<DashboardCountryDetail>>, NotFound>> HandleCountryDetail(
        string code,
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var detail = await store.GetCountryDetailAsync(code, since, until);
        if (detail is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<DashboardCountryDetail> { Data = detail, Meta = new ResponseMeta() });
    }

    private static async Task<Ok<PaginatedResponse<DashboardEndpointStats>>> HandleEndpoints(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        int limit = 50, DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var eps = await store.GetEndpointStatsAsync(limit, since, until);
        return TypedResults.Ok(new PaginatedResponse<DashboardEndpointStats>
        {
            Data = eps,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = eps.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<DashboardEndpointDetail>>, NotFound>> HandleEndpointDetail(
        string method, string path,
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var detail = await store.GetEndpointDetailAsync(method, "/" + path, since, until);
        if (detail is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<DashboardEndpointDetail> { Data = detail, Meta = new ResponseMeta() });
    }

    private static async Task<Ok<PaginatedResponse<DashboardTopBotEntry>>> HandleTopBots(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        [FromServices] SignatureAggregateCache signatureCache,
        [FromServices] IClusterMembershipLookup? clusterLookup,
        int limit = 10, DateTime? since = null, DateTime? until = null,
        string? audience = null)
    {
        aggregateCache.MarkHit();
        var snapshot = aggregateCache.Current;
        // Aggregate-cache fast path only applies to the legacy bots-only call (no window,
        // no audience). When the caller asks for "all" or "humans" we go direct to the
        // event store -- the cache only holds the precomputed top bots.
        if (since is null && until is null && string.IsNullOrEmpty(audience)
            && snapshot.TopBots.Count >= limit
            && snapshot.ComputedAt != DateTime.MinValue)
        {
            var slice = snapshot.TopBots.Take(limit).ToList();
            slice = OverlayRiskVerdict(slice, clusterLookup);
            return TypedResults.Ok(new PaginatedResponse<DashboardTopBotEntry>
            {
                Data = slice,
                Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = slice.Count },
                Meta = new ResponseMeta()
            });
        }

        // No time window: serve straight from the gateway's in-memory write-through
        // SignatureAggregateCache. The cache is updated on every detection by
        // DetectionBroadcastMiddleware, so this is the canonical fresh view -- and
        // it's microseconds-vs-milliseconds compared to the SQL path. Remote-mode
        // dashboard hosts pay a sub-ms LAN round-trip for fresh, consistent data
        // instead of duplicating the cache locally. Time-windowed queries still
        // need the event store because the cache only holds the rolling top-N.
        if (since is null && until is null)
        {
            var cached = signatureCache.GetTopBots(
                page: 1,
                pageSize: limit,
                sortBy: "default",
                sortDir: "desc",
                filter: string.IsNullOrEmpty(audience) ? "bots" : audience);
            if (cached.Count > 0)
            {
                cached = OverlayRiskVerdict(cached, clusterLookup);
                return TypedResults.Ok(new PaginatedResponse<DashboardTopBotEntry>
                {
                    Data = cached,
                    Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = cached.Count },
                    Meta = new ResponseMeta()
                });
            }
        }

        var bots = await store.GetTopBotsAsync(limit, since, until, audience);

        // HitTrend is a runtime ring buffer that lives only in SignatureAggregateCache;
        // the DB stores raw detections, not per-minute counts. Without this overlay,
        // remote-mode dashboard hosts (e.g. stylobot.net's website) request a 24h-
        // windowed top-bots and get DB rows with hitTrend=[] -- so every row's
        // Live Activity sparkline renders as a flat baseline regardless of how
        // much fresh traffic the signature is actually getting. Splice in the live
        // trend from the gateway's in-memory cache for any signature we still hold.
        for (int i = 0; i < bots.Count; i++)
        {
            if ((bots[i].HitTrend is null || bots[i].HitTrend.Length == 0)
                && signatureCache.TryGetHitTrend(bots[i].PrimarySignature, out var trend))
            {
                bots[i] = bots[i] with { HitTrend = trend };
            }
        }

        bots = OverlayRiskVerdict(bots, clusterLookup);

        return TypedResults.Ok(new PaginatedResponse<DashboardTopBotEntry>
        {
            Data = bots,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = bots.Count },
            Meta = new ResponseMeta()
        });
    }

    /// <summary>
    ///     Compose a unified <see cref="SignatureRiskVerdict"/> per row from the inputs
    ///     we already have at the API edge (probability, confidence, threat, bot type,
    ///     cluster membership) and overlay the verdict's clamped ThreatBand back onto
    ///     the entry. The historical patchiness this fixes: DB-sourced rows carry the
    ///     raw threat band from the latest detection event, with no friendly-pin gate
    ///     -- so a verified Googlebot or Mastodon-fanout member can present with
    ///     ThreatBand=VeryHigh even when the row's own RiskBand was already clamped to
    ///     Low. The composer applies hostile-pin then friendly-pin to both axes
    ///     together, so the per-row threat pill now tracks the same friendly/hostile
    ///     decision the rest of the dashboard already shows.
    ///     <para>
    ///     Latches we don't have at this layer yet (FriendlyVerified persisted on the
    ///     aggregate, archetype anchor) stay defaulted; cluster membership + declared-
    ///     friendly-bot-type are enough to fix the operator-visible Mastodon / bingbot
    ///     cases this turn. Future commits add the remaining input wiring without
    ///     touching this overlay site.
    ///     </para>
    /// </summary>
    private static List<DashboardTopBotEntry> OverlayRiskVerdict(
        List<DashboardTopBotEntry> bots,
        IClusterMembershipLookup? clusterLookup)
    {
        for (int i = 0; i < bots.Count; i++)
        {
            var entry = bots[i];
            var cluster = clusterLookup?.TryGetClusterForSignature(entry.PrimarySignature);
            var ledgerBotType = ParseBotType(entry.BotType);
            var rawThreatBand = ParseThreatBand(entry.ThreatBand);

            // BotType propagation upstream can silently overwrite a YAML-matched
            // "SearchEngine" with HeuristicEarly's generic "Scraper" guess (see
            // DetectionLedgerExtensions:88). The dashboard already shows bingbot /
            // googlebot / Mastodon rows with BotType=Scraper despite their BotName
            // resolving to a known friendly pattern. Mirror DetermineRiskBand's
            // YAML-name fallback so the friendly pin fires for THOSE rows too;
            // without it the composer only catches the ledger-friendly cases and
            // the operator-visible bingbot-VeryHigh problem persists.
            var yamlBotType = ParseBotType(BotPatternLoader.Default.FindBotTypeByName(entry.BotName));
            var isFriendlyType = BotTypeClassification.IsFriendly(ledgerBotType)
                                 || BotTypeClassification.IsFriendly(yamlBotType);

            var inputs = new SignatureRiskInputs
            {
                PrimarySignature = entry.PrimarySignature,
                BotProbability = entry.BotProbability,
                Confidence = entry.Confidence,
                RawThreatScore = entry.ThreatScore ?? 0,
                RawThreatBand = rawThreatBand,
                FriendlyVerified = false,                   // not on aggregate yet
                ConfirmedBad = false,                       // not on aggregate yet
                DeclaredBot = entry.IsKnownBot || !string.IsNullOrEmpty(entry.BotName),
                BotName = entry.BotName,
                BotType = entry.BotType,
                IsFriendlyBotType = isFriendlyType,
                ClusterType = cluster?.Type,
                ClusterId = cluster?.ClusterId,
                ClusterLabel = cluster?.Label,
                ClusterAverageThreatScore = cluster?.AverageThreatScore,
            };

            var verdict = SignatureRiskVerdictComposer.Compose(inputs);

            // Only rewrite when a pin fired; otherwise leave the raw store value
            // alone so we don't regress the existing default behaviour for rows
            // the composer has no opinion about.
            if (verdict.FriendlyPinFired || verdict.HostilePinFired)
            {
                bots[i] = entry with { ThreatBand = verdict.ThreatBand.ToString() };
            }
        }
        return bots;
    }

    private static BotType? ParseBotType(string? raw)
        => string.IsNullOrEmpty(raw) || !Enum.TryParse<BotType>(raw, true, out var v) ? null : v;

    private static ThreatBand? ParseThreatBand(string? raw)
        => string.IsNullOrEmpty(raw) || !Enum.TryParse<ThreatBand>(raw, true, out var v) ? null : v;

    private static async Task<Ok<PaginatedResponse<ThreatEntry>>> HandleThreats(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        int limit = 20, DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var snapshot = aggregateCache.Current;
        if (since is null && until is null
            && snapshot.Threats.Count >= limit
            && snapshot.ComputedAt != DateTime.MinValue)
        {
            var slice = snapshot.Threats.Take(limit).ToList();
            return TypedResults.Ok(new PaginatedResponse<ThreatEntry>
            {
                Data = slice,
                Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = slice.Count },
                Meta = new ResponseMeta()
            });
        }

        var threats = await store.GetThreatsAsync(limit, since, until);
        return TypedResults.Ok(new PaginatedResponse<ThreatEntry>
        {
            Data = threats,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = threats.Count },
            Meta = new ResponseMeta()
        });
    }
}
