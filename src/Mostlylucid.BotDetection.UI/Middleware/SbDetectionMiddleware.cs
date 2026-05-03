using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Lightweight middleware that ensures detection data is available in <c>HttpContext.Items</c>
///     before the request reaches Razor views and tag helpers.
///     <para>
///     In inline mode (BotDetectionMiddleware present), this is a no-op - Items already have data.
///     In YARP-header mode, this is also a no-op - tag helpers read headers directly.
///     In API mode (configured via <c>AddStyloBotApiMode</c>), this calls the detection API and
///     caches the result in Items so that synchronous <c>sb-*</c> tag helpers find it.
///     </para>
/// </summary>
/// <example>
///     Setup for standalone pages that need detection data from a remote gateway:
///     <code>
///     builder.Services.AddBotDetection();
///     builder.Services.AddStyloBotUI();
///     builder.Services.AddStyloBotApiMode("http://gateway:8080/api/v1/me");
///     // ...
///     app.UseMiddleware&lt;SbDetectionMiddleware&gt;();
///     app.MapControllers();
///     </code>
/// </example>
public class SbDetectionMiddleware(RequestDelegate next, DetectionDataExtractor extractor)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await extractor.EnsurePopulatedAsync(context);
        await next(context);
    }
}
