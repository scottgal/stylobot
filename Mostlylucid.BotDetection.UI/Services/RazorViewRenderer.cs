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
        var actionContext = new ActionContext(httpContext, httpContext.GetRouteData(), new ActionDescriptor());

        var viewResult = _viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: true);
        if (!viewResult.Success)
            throw new InvalidOperationException($"Could not find Razor view '{viewPath}'. Searched: {string.Join(", ", viewResult.SearchedLocations ?? [])}");

        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model
        };

        var tempData = new TempDataDictionary(httpContext, _tempDataProvider);

        await using var writer = new StringWriter();
        var viewContext = new ViewContext(actionContext, viewResult.View, viewData, tempData, writer, new HtmlHelperOptions());
        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }
}
