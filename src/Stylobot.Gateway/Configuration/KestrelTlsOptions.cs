using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Stylobot.Gateway.Configuration;

public static class KestrelTlsOptions
{
    public static void UseHttpsWithTlsCapture(
        this ListenOptions listenOptions,
        X509Certificate2 certificate,
        ILogger logger)
    {
        // TLS metadata is read from ITlsHandshakeFeature in middleware after handshake completes.
        listenOptions.UseHttps(certificate);
    }
}

public static class ConnectionContextTlsExtensions
{
    public static void CopyTlsMetadataToHttpContext(this HttpContext httpContext)
    {
        var tls = httpContext.Features.Get<ITlsHandshakeFeature>();
        if (tls == null) return;

        httpContext.Items[GatewayHttpContextKeys.TlsProtocol] = MapProtocol(tls.Protocol);
        httpContext.Items[GatewayHttpContextKeys.TlsCipherSuite] = tls.NegotiatedCipherSuite.ToString();
    }

    #pragma warning disable SYSLIB0039
    private static string MapProtocol(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls   => "TLSv1.0",
        SslProtocols.Tls11 => "TLSv1.1",
        SslProtocols.Tls12 => "TLSv1.2",
        SslProtocols.Tls13 => "TLSv1.3",
        _                  => protocol.ToString()
    };
    #pragma warning restore SYSLIB0039
}
