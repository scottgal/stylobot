using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Proxy;

namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Shared client-IP resolution, extracted from <see cref="Orchestration.Atoms.IpAtom"/>
///     so other atoms (e.g. <see cref="Orchestration.Atoms.WebhookSensor"/>) resolve the same
///     CLIENT ip -- never the raw TCP peer alone -- without duplicating the fallback chain.
/// </summary>
/// <remarks>
///     Prefers <see cref="IProxyEnvironment.GetRealClientIp"/> when a proxy environment is
///     available (topology-aware header selection); otherwise falls back to the connection
///     peer IP with an <c>X-Forwarded-For</c> override when the peer itself is a local/private
///     address (i.e. the request arrived through an untrusted-but-unconfigured proxy hop).
/// </remarks>
public static class ClientIpResolver
{
    public static string Resolve(HttpContext context, IProxyEnvironment? proxyEnvironment = null)
    {
        if (proxyEnvironment is not null)
            return proxyEnvironment.GetRealClientIp(context);

        var connectionIp = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(connectionIp) && !NetworkHelper.IsLocalIp(connectionIp))
            return connectionIp;

        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            var firstIp = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!string.IsNullOrEmpty(firstIp) && !NetworkHelper.IsLocalIp(firstIp))
                return firstIp;
        }

        return connectionIp;
    }
}
