using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.GeoDetection.Middleware;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Extensions;

/// <summary>
///     Extension methods for country-based endpoint routing
/// </summary>
public static class EndpointGeoRoutingExtensions
{
    /// <summary>
    ///     Route to different endpoints based on country
    /// </summary>
    public static RouteGroupBuilder MapByCountry(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Action<CountryRouteBuilder> configure)
    {
        var group = endpoints.MapGroup(pattern);
        var builder = new CountryRouteBuilder(group);
        configure(builder);
        return group;
    }

    /// <summary>
    ///     Get country-specific content based on visitor's location
    /// </summary>
    public static RouteHandlerBuilder ServeByCountry(
        this RouteHandlerBuilder builder,
        Dictionary<string, Func<Task<IResult>>> countryHandlers,
        Func<Task<IResult>>? defaultHandler = null)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var country = httpContext.GetCountryCode();

            if (country != null && countryHandlers.TryGetValue(country, out var handler)) return await handler();

            if (defaultHandler != null) return await defaultHandler();

            return await next(context);
        });
    }

    /// <summary>
    ///     Redirect to country-specific path
    /// </summary>
    public static RouteHandlerBuilder RedirectByCountry(
        this RouteHandlerBuilder builder,
        Dictionary<string, string> countryPaths,
        string? defaultPath = null)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var country = httpContext.GetCountryCode();

            if (country != null && countryPaths.TryGetValue(country, out var path)) return Results.Redirect(path);

            if (defaultPath != null) return Results.Redirect(defaultPath);

            return await next(context);
        });
    }

    /// <summary>
    ///     Require request to be from specific countries
    /// </summary>
    public static RouteHandlerBuilder RequireCountry(
        this RouteHandlerBuilder builder,
        params string[] countryCodes)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var location = httpContext.Items[GeoRoutingMiddleware.GeoLocationKey] as GeoLocation;

            if (location == null)
                // No geo data available, allow through
                return await next(context);

            if (!countryCodes.Contains(location.CountryCode, StringComparer.OrdinalIgnoreCase))
                return Results.StatusCode(451); // Unavailable For Legal Reasons

            return await next(context);
        });
    }

    /// <summary>
    ///     Block requests from specific countries
    /// </summary>
    public static RouteHandlerBuilder BlockCountries(
        this RouteHandlerBuilder builder,
        params string[] countryCodes)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var location = httpContext.Items[GeoRoutingMiddleware.GeoLocationKey] as GeoLocation;

            if (location == null) return await next(context);

            if (countryCodes.Contains(location.CountryCode, StringComparer.OrdinalIgnoreCase))
                return Results.StatusCode(451);

            return await next(context);
        });
    }

    /// <summary>
    ///     Route requests based on country
    /// </summary>
    public static RouteHandlerBuilder RouteByCountry(
        this RouteHandlerBuilder builder,
        Dictionary<string, string> countryRoutes)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var location = httpContext.Items[GeoRoutingMiddleware.GeoLocationKey] as GeoLocation;

            if (location != null && countryRoutes.TryGetValue(location.CountryCode, out var route))
                // Store the target route in context for downstream handling
                httpContext.Items["TargetRoute"] = route;

            return await next(context);
        });
    }

    /// <summary>
    ///     Get geo location from context
    /// </summary>
    public static GeoLocation? GetGeoLocation(this HttpContext context)
    {
        return context.Items[GeoRoutingMiddleware.GeoLocationKey] as GeoLocation;
    }

    /// <summary>
    ///     Get country code from context
    /// </summary>
    public static string? GetCountryCode(this HttpContext context)
    {
        return context.GetGeoLocation()?.CountryCode;
    }
}

/// <summary>
///     Builder for country-based routing
/// </summary>
public class CountryRouteBuilder
{
    private readonly Dictionary<string, Delegate> _countryHandlers = new();
    private readonly RouteGroupBuilder _group;

    public CountryRouteBuilder(RouteGroupBuilder group)
    {
        _group = group;
    }

    /// <summary>
    ///     Map handler for specific country
    /// </summary>
    public CountryRouteBuilder ForCountry(string countryCode, Delegate handler)
    {
        _countryHandlers[countryCode.ToUpperInvariant()] = handler;

        _group.MapGet("", async (HttpContext context) =>
        {
            var country = context.GetCountryCode()?.ToUpperInvariant();

            if (country != null && _countryHandlers.TryGetValue(country, out var countryHandler))
                return await InvokeHandler(countryHandler, context);

            return Results.NotFound("No route configured for your country");
        });

        return this;
    }

    /// <summary>
    ///     Map default handler for countries without specific routes
    /// </summary>
    public CountryRouteBuilder Default(Delegate handler)
    {
        _group.MapGet("", async (HttpContext context) =>
        {
            var country = context.GetCountryCode()?.ToUpperInvariant();

            if (country != null && _countryHandlers.TryGetValue(country, out var countryHandler))
                return await InvokeHandler(countryHandler, context);

            return await InvokeHandler(handler, context);
        });

        return this;
    }

    private async Task<IResult> InvokeHandler(Delegate handler, HttpContext context)
    {
        var result = handler.DynamicInvoke(context);

        if (result is Task<IResult> taskResult) return await taskResult;

        if (result is IResult syncResult) return syncResult;

        if (result is Task<string> taskString) return Results.Content(await taskString);

        if (result is string str) return Results.Content(str);

        return Results.Ok(result);
    }
}