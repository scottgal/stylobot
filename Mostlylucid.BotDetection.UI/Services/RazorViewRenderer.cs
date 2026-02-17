using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Renders a Razor view (.cshtml) to a string, for use from middleware.
///     Sets up a dummy endpoint so AnchorTagHelper uses endpoint routing (LinkGenerator)
///     instead of legacy IRouter-based URL generation.
/// </summary>
public sealed class RazorViewRenderer
{
    private readonly IRazorViewEngine _viewEngine;
    private readonly ITempDataProvider _tempDataProvider;

    public RazorViewRenderer(IRazorViewEngine viewEngine, ITempDataProvider tempDataProvider)
    {
        _viewEngine = viewEngine;
        _tempDataProvider = tempDataProvider;
    }

    /// <summary>
    ///     Renders the specified view with the given model and returns the HTML string.
    /// </summary>
    /// <param name="viewPath">Absolute view path, e.g. "/Views/Dashboard/Index.cshtml"</param>
    /// <param name="model">The view model</param>
    /// <param name="httpContext">The current HTTP context</param>
    public async Task<string> RenderViewToStringAsync(string viewPath, object model, HttpContext httpContext)
    {
        // Ensure endpoint routing is available for AnchorTagHelper.
        // When rendering from middleware, no MVC endpoint is matched, so UrlHelperFactory
        // falls back to legacy IRouter-based UrlHelper which fails. Setting a dummy endpoint
        // forces it to use EndpointRoutingUrlHelper (backed by LinkGenerator) instead.
        var originalEndpoint = httpContext.GetEndpoint();
        if (originalEndpoint == null)
            httpContext.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(), "DashboardMiddleware"));

        try
        {
            var routeData = httpContext.GetRouteData() ?? new RouteData();
            var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

            var viewResult = _viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: true);
            if (!viewResult.Success)
                throw new InvalidOperationException(
                    $"Could not find Razor view '{viewPath}'. Searched: {string.Join(", ", viewResult.SearchedLocations ?? [])}");

            var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
            {
                Model = model
            };

            var tempData = new TempDataDictionary(httpContext, _tempDataProvider);

            await using var writer = new StringWriter();
            var viewContext = new ViewContext(actionContext, viewResult.View, viewData, tempData, writer,
                new HtmlHelperOptions());
            await viewResult.View.RenderAsync(viewContext);
            return writer.ToString();
        }
        finally
        {
            // Restore original endpoint state
            if (originalEndpoint == null)
                httpContext.SetEndpoint(null);
        }
    }
}
