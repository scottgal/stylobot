using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Mostlylucid.GeoDetection.Extensions;

namespace Mostlylucid.GeoDetection.Filters;

/// <summary>
///     Attribute to route MVC actions based on visitor's country
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class GeoRouteAttribute : ActionFilterAttribute
{
    private Dictionary<string, string>? _actionMap;
    private Dictionary<string, string>? _routeMap;

    private Dictionary<string, string>? _viewMap;

    /// <summary>
    ///     Country-specific view mapping
    ///     Example: "CN:china-view,RU:russia-view"
    /// </summary>
    public string? CountryViews { get; set; }

    /// <summary>
    ///     Country-specific action mapping
    ///     Example: "CN:ChinaAction,RU:RussiaAction"
    /// </summary>
    public string? CountryActions { get; set; }

    /// <summary>
    ///     Country-specific route mapping
    ///     Example: "CN:/cn/home,RU:/ru/home"
    /// </summary>
    public string? CountryRoutes { get; set; }

    /// <summary>
    ///     Default view if no country match
    /// </summary>
    public string? DefaultView { get; set; }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var countryCode = context.HttpContext.GetCountryCode();

        if (string.IsNullOrEmpty(countryCode))
        {
            base.OnActionExecuting(context);
            return;
        }

        // Initialize maps
        _viewMap = ParseMapping(CountryViews);
        _actionMap = ParseMapping(CountryActions);
        _routeMap = ParseMapping(CountryRoutes);

        // Check for route redirect
        if (_routeMap != null && _routeMap.TryGetValue(countryCode, out var route))
        {
            context.Result = new RedirectResult(route);
            return;
        }

        // Check for action redirect
        if (_actionMap != null && _actionMap.TryGetValue(countryCode, out var action))
            context.RouteData.Values["action"] = action;

        // Store view preference for OnActionExecuted
        if (_viewMap != null && _viewMap.TryGetValue(countryCode, out var view))
            context.HttpContext.Items["GeoRouteView"] = view;
        else if (!string.IsNullOrEmpty(DefaultView)) context.HttpContext.Items["GeoRouteView"] = DefaultView;

        base.OnActionExecuting(context);
    }

    public override void OnActionExecuted(ActionExecutedContext context)
    {
        if (context.Result is ViewResult viewResult)
        {
            var preferredView = context.HttpContext.Items["GeoRouteView"] as string;
            if (!string.IsNullOrEmpty(preferredView)) viewResult.ViewName = preferredView;
        }

        base.OnActionExecuted(context);
    }

    private Dictionary<string, string>? ParseMapping(string? mapping)
    {
        if (string.IsNullOrEmpty(mapping))
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pairs = mapping.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var parts = pair.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) result[parts[0].Trim()] = parts[1].Trim();
        }

        return result.Any() ? result : null;
    }
}

/// <summary>
///     Attribute to show country-specific content in MVC views
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class ServeByCountryAttribute : ActionFilterAttribute
{
    private readonly Dictionary<string, string> _countryContent = new();

    /// <summary>
    ///     Add country-specific content
    /// </summary>
    public ServeByCountryAttribute(params string[] countryMappings)
    {
        foreach (var mapping in countryMappings)
        {
            var parts = mapping.Split(':', 2);
            if (parts.Length == 2) _countryContent[parts[0].Trim().ToUpperInvariant()] = parts[1].Trim();
        }
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var countryCode = context.HttpContext.GetCountryCode();

        if (!string.IsNullOrEmpty(countryCode) &&
            _countryContent.TryGetValue(countryCode, out var content))
        {
            context.Result = new ContentResult
            {
                Content = content,
                ContentType = "text/html",
                StatusCode = 200
            };
            return;
        }

        base.OnActionExecuting(context);
    }
}