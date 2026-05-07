using Microsoft.Extensions.Options;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Services;

namespace Stylobot.Gateway.Middleware;

public class ProfileCaptureMiddleware(
    RequestDelegate next,
    IOptions<ProfileModeOptions> options,
    ProfileAnalysisChannel channel)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (options.Value.Enabled)
            channel.TryEnqueue(ProfileRequestSnapshot.From(ctx));

        await next(ctx);
    }
}
