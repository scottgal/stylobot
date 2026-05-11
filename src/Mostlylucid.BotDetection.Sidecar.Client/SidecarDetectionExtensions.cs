using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Sidecar.Client;

public static class SidecarDetectionExtensions
{
    public static IServiceCollection AddSidecarBotDetection(
        this IServiceCollection services,
        Action<SidecarClientOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SidecarClientOptions>>().Value;
            return GrpcChannel.ForAddress(opts.Endpoint);
        });
        return services;
    }

    public static IApplicationBuilder UseSidecarBotDetection(this IApplicationBuilder app)
        => app.UseMiddleware<SidecarBotDetectionMiddleware>();
}
