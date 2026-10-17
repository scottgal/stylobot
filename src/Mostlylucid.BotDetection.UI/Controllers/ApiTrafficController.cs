using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Dashboard.Contracts;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Services.Traffic;

namespace Mostlylucid.BotDetection.UI.Controllers;

/// <summary>Only presentation and HTTP binding; backend queries own all traffic data.</summary>
public sealed class ApiTrafficController(TrafficApiClient client, StyloBotDashboardOptions options) : Controller
{
    internal static readonly object AuthorizedRequest = new();

    [HttpGet]
    public async Task<IActionResult> Index(string window = "24h", DateTimeOffset? from = null,
        DateTimeOffset? to = null, [FromQuery(Name = "domain")] string[]? domains = null,
        bool includeInternal = false, CancellationToken ct = default)
    {
        // The existing dashboard middleware owns every auth mode. Mapping this
        // controller without that gate must fail closed, including direct MVC routes.
        if (!options.UseApiTraffic || !HttpContext.Items.ContainsKey(AuthorizedRequest)) return StatusCode(403);
        var query = new TrafficQuery(window, from, to, domains, includeInternal);
        var result = ModelState.IsValid
            ? await client.ReadAsync(query, HttpContext.TraceIdentifier, ct)
            : new TrafficReadResult(TrafficReadState.InvalidQuery, TraceId: HttpContext.TraceIdentifier);
        var path = Request.PathBase + options.BasePath.TrimEnd('/') + "/traffic";
        var model = new ApiTrafficPageModel(path, query, result, options.SuppressVendorScripts);
        // Refreshing the same filters should not add duplicate browser history entries.
        if (Request.Headers["HX-Request"] == "true" &&
            Uri.TryCreate(Request.Headers["HX-Current-URL"], UriKind.Absolute, out var current) &&
            current.PathAndQuery == Request.PathBase + Request.Path + Request.QueryString)
            Response.Headers["HX-Push-Url"] = "false";
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Vary = "HX-Request, HX-History-Restore-Request";
        Response.StatusCode = result.State switch
        {
            TrafficReadState.Ready or TrafficReadState.Empty => 200,
            TrafficReadState.InvalidQuery => 400,
            _ => 503
        };
        return Request.Headers["HX-Request"] == "true" && Request.Headers["HX-History-Restore-Request"] != "true"
            ? PartialView("~/Views/StyloBot/Dashboard/ApiTraffic/_Content.cshtml", model)
            : View("~/Views/StyloBot/Dashboard/ApiTraffic/Index.cshtml", model);
    }
}

public sealed record ApiTrafficPageModel(string TrafficPath, TrafficQuery Query, TrafficReadResult Result, bool SuppressVendorScripts)
{
    public string BasePath => TrafficPath[..^"/traffic".Length];
}
