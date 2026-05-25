using Microsoft.AspNetCore.Builder;

namespace Mostlylucid.BotDetection.EndpointPolicies;

/// <summary>
///     Convenience extension to wire <see cref="DetectionPolicyMiddleware"/>
///     into the request pipeline. Place after the bot-detection middleware
///     so the detection result is on <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/>:
///     <code>
///     app.UseBotDetection();
///     app.UseDetectionPolicies();
///     app.UseRouting();
///     </code>
/// </summary>
public static class DetectionPolicyMiddlewareExtensions
{
    public static IApplicationBuilder UseDetectionPolicies(this IApplicationBuilder app)
        => app.UseMiddleware<DetectionPolicyMiddleware>();
}
