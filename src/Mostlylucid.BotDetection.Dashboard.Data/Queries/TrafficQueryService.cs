using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Dashboard.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Dashboard.Data.Queries;

/// <summary>Composes traffic from the existing durable store. Rendering never owns a second data source.</summary>
public sealed class TrafficQueryService(
    IDashboardEventStore store, IOptions<BotDetectionOptions> options, TimeProvider clock)
{
    public async Task<TrafficSnapshot> ReadAsync(TrafficQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var duration = query.Window switch
        {
            "1h" or "60m" => TimeSpan.FromHours(1), "6h" => TimeSpan.FromHours(6),
            "12h" => TimeSpan.FromHours(12), "24h" => TimeSpan.FromDays(1),
            "7d" => TimeSpan.FromDays(7), "30d" => TimeSpan.FromDays(30),
            "custom" => TimeSpan.Zero,
            _ => throw new TrafficQueryValidationException("Choose 1h, 6h, 12h, 24h, 7d, 30d or custom.", nameof(query.Window))
        };
        var end = query.Window == "custom" ? query.To : now;
        var start = query.Window == "custom" ? query.From : now - duration;
        if (start is null || end is null || start >= end || end - start > TimeSpan.FromDays(30))
            throw new TrafficQueryValidationException("The time range must be ordered and at most 30 days.", nameof(query.From));
        if (query.Limit is < 1 or > 200)
            throw new TrafficQueryValidationException("Limit must be between 1 and 200.", nameof(query.Limit));
        var domains = DashboardQueryParameters.NormalizeDomains(query.Domains) ?? [];
        if (domains.Count > 20 || domains.Any(d => d.Length > 253 || d.Any(char.IsControl)))
            throw new TrafficQueryValidationException("Supply at most 20 domains, each at most 253 characters.", nameof(query.Domains));
        var from = start.Value.UtcDateTime;
        var to = end.Value.UtcDateTime;
        var audience = query.IncludeInternal ? "all_incl_internal" : "all";
        var bucketMinutes = Math.Max(1, (int)Math.Ceiling((to - from).TotalMinutes / 72));
        ct.ThrowIfCancellationRequested();
        var bundle = await store.ComposeBatchAsync(new DashboardBatchRequest(from, to,
            [new(DatasetKind.SummaryStats), new(DatasetKind.TimeBuckets, BucketMinutes: bucketMinutes)],
            audience, Domains: domains), ct);
        ct.ThrowIfCancellationRequested();
        var rows = await store.GetDetectionsAsync(new DashboardFilter
        {
            StartTime = from, EndTime = to, Domains = domains,
            Limit = query.Limit, EndTimeExclusive = true, AudienceFilter = audience
        }, ct);
        // A missing required slice is a failed read, never an honest zero-traffic snapshot.
        var summary = bundle.Summary ?? throw new InvalidOperationException("Traffic summary was not returned by the store.");
        var timeline = bundle.TimeBuckets ?? throw new InvalidOperationException("Traffic timeline was not returned by the store.");
        var floor = options.Value.Classification.BotFloor;
        return new TrafficSnapshot(clock.GetUtcNow(), start.Value.ToUniversalTime(), end.Value.ToUniversalTime(),
            query.Window, domains, query.IncludeInternal,
            new(summary.TotalRequests, summary.HumanRequests, summary.BotRequests, summary.UncertainRequests),
            timeline.Select(p => new TrafficBucket(Utc(p.Timestamp), p.TotalCount, p.HumanCount, p.BotCount)).ToArray(),
            rows.Select(p => new TrafficDetection(Utc(p.Timestamp), p.Method, p.Path,
                p.BotProbability >= floor, p.BotProbability, p.RiskBand, p.BotName, p.Domain)).ToArray());
    }

    private static DateTimeOffset Utc(DateTime value) => new(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public sealed class TrafficQueryValidationException(string message, string parameter) : ArgumentException(message, parameter);
