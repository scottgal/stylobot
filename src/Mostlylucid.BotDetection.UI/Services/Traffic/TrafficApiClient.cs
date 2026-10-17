using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard.Contracts;

namespace Mostlylucid.BotDetection.UI.Services.Traffic;

public enum TrafficReadState { Ready, Empty, Unavailable, AccessDenied, InvalidQuery }
public sealed record TrafficReadResult(TrafficReadState State, TrafficSnapshot? Snapshot = null, string? TraceId = null);

/// <summary>Typed HTTP boundary. Errors and cancellation can never become successful empty data.</summary>
public sealed class TrafficApiClient(HttpClient http, ILogger<TrafficApiClient> logger)
{
    public async Task<TrafficReadResult> ReadAsync(TrafficQuery query, string traceId, CancellationToken ct)
    {
        var parameters = new List<KeyValuePair<string, string?>>
        {
            new("window", query.Window), new("includeInternal", query.IncludeInternal ? "true" : "false"),
            new("limit", query.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        if (query.From is { } from) parameters.Add(new("from", from.ToString("O")));
        if (query.To is { } to) parameters.Add(new("to", to.ToString("O")));
        foreach (var domain in query.Domains ?? []) parameters.Add(new("domain", domain));
        var path = QueryHelpers.AddQueryString("api/v1/dashboard/traffic", parameters);
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (http.Timeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(http.Timeout);
        try
        {
            using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var upstreamTrace = response.Headers.TryGetValues("X-StyloBot-TraceId", out var values) ? values.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Traffic API read returned {StatusCode} after {ElapsedMs:F0}ms. TraceId: {TraceId}; UpstreamTraceId: {UpstreamTraceId}",
                    (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, traceId, upstreamTrace);
                return new(response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => TrafficReadState.InvalidQuery,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => TrafficReadState.AccessDenied,
                    _ => TrafficReadState.Unavailable
                }, TraceId: upstreamTrace ?? traceId);
            }
            var snapshot = await response.Content.ReadFromJsonAsync(DashboardJsonContext.Default.TrafficSnapshot, timeout.Token)
                ?? throw new JsonException("Traffic API returned a null snapshot.");
            if (snapshot.Summary is null || snapshot.Timeline is null || snapshot.Detections is null ||
                snapshot.Domains is null || string.IsNullOrWhiteSpace(snapshot.Window) ||
                snapshot.FromUtc >= snapshot.ToUtc || snapshot.GeneratedAtUtc == default ||
                snapshot.Summary.Requests < 0 || snapshot.Timeline.Any(b => b is null || b.Requests < 0) ||
                snapshot.Detections.Any(d => d is null || d.Path is null || d.Method is null || d.RiskBand is null))
                throw new JsonException("Traffic API returned an incomplete snapshot.");
            return new(snapshot.Summary.Requests == 0 ? TrafficReadState.Empty : TrafficReadState.Ready, snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Traffic API read failed after {ElapsedMs:F0}ms. TraceId: {TraceId}",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, traceId);
            return new(TrafficReadState.Unavailable, TraceId: traceId);
        }
    }
}
