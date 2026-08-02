using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Optional host capability for the endpoint widget's initial server render.
///     A host whose normal <see cref="Services.IDashboardEventStore"/> is deliberately
///     stale-while-revalidating can attach a bounded, authoritative reader to the
///     request, so the first HTML response contains real endpoint rows rather than that
///     decorator's cold-cache placeholder. Hosts that do not attach this capability retain
///     the normal event-store path.
/// </summary>
public interface IDashboardEndpointsFirstPaintReader
{
    /// <summary>Read the initial endpoint slice for a server-rendered dashboard request.</summary>
    Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
        int count,
        DateTime? startTime,
        DateTime? endTime,
        string? audienceFilter,
        IReadOnlyList<string>? domains,
        CancellationToken cancellationToken = default);
}

/// <summary>Request-scoped handoff for <see cref="IDashboardEndpointsFirstPaintReader"/>.</summary>
public static class DashboardEndpointsFirstPaintContext
{
    private static readonly object ReaderKey = new();

    /// <summary>Attach the host's authoritative first-paint reader to this dashboard request.</summary>
    public static void Set(HttpContext context, IDashboardEndpointsFirstPaintReader reader) =>
        context.Items[ReaderKey] = reader;

    /// <summary>Get the optional first-paint reader attached by the host.</summary>
    public static IDashboardEndpointsFirstPaintReader? Get(HttpContext? context) =>
        context is not null && context.Items.TryGetValue(ReaderKey, out var reader)
            ? reader as IDashboardEndpointsFirstPaintReader
            : null;
}
