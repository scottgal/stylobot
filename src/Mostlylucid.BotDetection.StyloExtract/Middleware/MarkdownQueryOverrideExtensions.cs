using Microsoft.AspNetCore.Builder;

namespace Mostlylucid.BotDetection.StyloExtract.Middleware;

public static class MarkdownQueryOverrideExtensions
{
    /// <summary>Enables the configured <c>?markdown=true</c> test representation.</summary>
    public static IApplicationBuilder UseStyloExtractMarkdownQueryOverride(this IApplicationBuilder app) =>
        app.UseMiddleware<MarkdownQueryOverrideMiddleware>();
}
