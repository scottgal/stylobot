using System.Net;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Api.Models;

namespace Mostlylucid.BotDetection.Api.Bridge;

public static class SyntheticHttpContext
{
    public static HttpContext FromDetectRequest(DetectRequest request)
    {
        var context = new DefaultHttpContext();

        var pathAndQuery = request.Path;
        var queryIndex = pathAndQuery.IndexOf('?');
        if (queryIndex >= 0)
        {
            context.Request.Path = pathAndQuery[..queryIndex];
            context.Request.QueryString = new QueryString(pathAndQuery[queryIndex..]);
        }
        else
        {
            context.Request.Path = pathAndQuery;
        }

        context.Request.Method = request.Method;
        context.Request.Scheme = request.Protocol;

        foreach (var (key, value) in request.Headers)
        {
            context.Request.Headers[key] = value;
        }

        if (IPAddress.TryParse(request.RemoteIp, out var ip))
        {
            context.Connection.RemoteIpAddress = ip;
        }

        if (request.Tls is not null)
        {
            context.Items["BotDetection.TlsInfo"] = request.Tls;

            // Project TLS fields into the canonical headers that TlsFingerprintContributor
            // already reads. Same code path the gateway uses for proxy-forwarded TLS, so
            // a request arriving via the Caddy plugin / sidecar gRPC and a request arriving
            // direct-to-gateway with proxy headers produce identical signals downstream.
            // Caller's headers win if they already supplied a value (don't clobber).
            void SetIfMissing(string name, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) && !context.Request.Headers.ContainsKey(name))
                    context.Request.Headers[name] = value;
            }

            SetIfMissing("X-Client-TLS-Version", request.Tls.Version);
            SetIfMissing("X-Client-TLS-Cipher", request.Tls.Cipher);
            SetIfMissing("X-TLS-Protocol", request.Tls.Version);
            SetIfMissing("X-TLS-Cipher", request.Tls.Cipher);
            SetIfMissing("X-JA3-Hash", request.Tls.Ja3);
            SetIfMissing("X-JA4", request.Tls.Ja4);
        }

        context.TraceIdentifier = Guid.NewGuid().ToString("N")[..12];

        return context;
    }
}
