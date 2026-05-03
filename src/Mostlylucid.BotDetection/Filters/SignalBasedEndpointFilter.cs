using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Extensions;

namespace Mostlylucid.BotDetection.Filters;

/// <summary>
///     Endpoint filter that blocks requests based on specific detection signal values.
///     Use with the fluent API: <c>.RequireSignal("geo.country_code", "US", "CA")</c>
///     or <c>.BlockIfSignal("ip.is_datacenter", true)</c>.
/// </summary>
/// <example>
///     // Only allow US and Canadian traffic
///     app.MapGet("/api/domestic", () => "data")
///        .RequireSignal("geo.country_code", "US", "CA");
///
///     // Block datacenter traffic
///     app.MapGet("/api/organic", () => "data")
///        .BlockIfSignal&lt;bool&gt;("ip.is_datacenter", true);
///
///     // Require low bot clustering density
///     app.MapGet("/api/human-only", () => "data")
///        .BlockIfSignalAbove("cluster.temporal_density", 0.8);
/// </example>
public class RequireSignalEndpointFilter<T> : IEndpointFilter
{
    private readonly string _signalKey;
    private readonly HashSet<T> _allowedValues;
    private readonly int _statusCode;
    private readonly string? _message;

    public RequireSignalEndpointFilter(string signalKey, IEnumerable<T> allowedValues,
        int statusCode = 403, string? message = null)
    {
        _signalKey = signalKey;
        _allowedValues = new HashSet<T>(allowedValues);
        _statusCode = statusCode;
        _message = message;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        // If signal is missing, allow through (detector may not have run)
        if (!httpContext.HasSignal(_signalKey))
            return await next(context);

        var value = httpContext.GetSignal<T>(_signalKey);

        if (value != null && _allowedValues.Contains(value))
            return await next(context);

        return Results.Json(new
        {
            error = _message ?? "Access denied",
            signal = _signalKey,
            blocked = true
        }, statusCode: _statusCode);
    }
}

/// <summary>
///     Endpoint filter that blocks requests when a signal matches a specific value.
/// </summary>
public class BlockIfSignalEndpointFilter<T> : IEndpointFilter
{
    private readonly string _signalKey;
    private readonly T _blockValue;
    private readonly int _statusCode;
    private readonly string? _message;

    public BlockIfSignalEndpointFilter(string signalKey, T blockValue,
        int statusCode = 403, string? message = null)
    {
        _signalKey = signalKey;
        _blockValue = blockValue;
        _statusCode = statusCode;
        _message = message;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var value = httpContext.GetSignal<T>(_signalKey);

        if (value != null && EqualityComparer<T>.Default.Equals(value, _blockValue))
            return Results.Json(new
            {
                error = _message ?? "Access denied",
                signal = _signalKey,
                blocked = true
            }, statusCode: _statusCode);

        return await next(context);
    }
}

/// <summary>
///     Endpoint filter that blocks requests when a numeric signal exceeds a threshold.
/// </summary>
public class BlockIfSignalAboveEndpointFilter : IEndpointFilter
{
    private readonly string _signalKey;
    private readonly double _threshold;
    private readonly int _statusCode;
    private readonly string? _message;

    public BlockIfSignalAboveEndpointFilter(string signalKey, double threshold,
        int statusCode = 403, string? message = null)
    {
        _signalKey = signalKey;
        _threshold = threshold;
        _statusCode = statusCode;
        _message = message;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var value = httpContext.GetSignal<double>(_signalKey);

        if (value > _threshold)
            return Results.Json(new
            {
                error = _message ?? "Access denied",
                signal = _signalKey,
                threshold = _threshold,
                blocked = true
            }, statusCode: _statusCode);

        return await next(context);
    }
}
