using Microsoft.AspNetCore.Builder;
using Mostlylucid.StyloWall.Middleware;

namespace Mostlylucid.StyloWall.Extensions;

public static class StyloWallApplicationBuilderExtensions
{
    public static IApplicationBuilder UseStyloWall(this IApplicationBuilder app)
    {
        return app.UseMiddleware<StyloWallMiddleware>();
    }
}
