using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Yarp;

namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Extension methods for adding YARP learning mode to the ASP.NET Core pipeline.
/// </summary>
public static class YarpLearningExtensions
{
    /// <summary>
    ///     Adds YARP learning mode services to the service collection.
    ///     This registers the signature writer as a singleton.
    /// </summary>
    public static IServiceCollection AddYarpLearningMode(this IServiceCollection services)
    {
        services.AddSingleton<IYarpSignatureWriter, YarpSignatureWriter>();
        return services;
    }

    /// <summary>
    ///     Adds YARP learning mode middleware to the pipeline.
    ///     Must be called AFTER UseBotDetection() to capture detection results.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         IMPORTANT: This middleware must run AFTER bot detection middleware
    ///         because it reads detection results from HttpContext.Items.
    ///     </para>
    ///     <para>
    ///         Typical middleware order:
    ///         <code>
    ///         app.UseHttpsRedirection();
    ///         app.UseBotDetection();        // Detects bots first
    ///         app.UseYarpLearningMode();    // Then captures signatures
    ///         app.UseAuthorization();
    ///         app.MapControllers();
    ///         </code>
    ///     </para>
    /// </remarks>
    public static IApplicationBuilder UseYarpLearningMode(this IApplicationBuilder app)
    {
        return app.UseMiddleware<YarpLearningMiddleware>();
    }
}