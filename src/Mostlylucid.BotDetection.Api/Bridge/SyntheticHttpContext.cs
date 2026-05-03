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
        }

        context.TraceIdentifier = Guid.NewGuid().ToString("N")[..12];

        return context;
    }
}
