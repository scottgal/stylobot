using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Mostlylucid.YarpGateway.Transforms;

/// <summary>
///     YARP transform that extracts TLS/TCP/HTTP2 metadata for bot detection fingerprinting.
///
///     This transform runs on the request pipeline and adds headers with network-layer metadata
///     that downstream bot detection contributors (TlsFingerprintContributor, TcpIpFingerprintContributor,
///     Http2FingerprintContributor) can analyze.
/// </summary>
public class TlsFingerprintingTransform : RequestTransform
{
    public override ValueTask ApplyAsync(RequestTransformContext context)
    {
        var httpContext = context.HttpContext;

        try
        {
            // ============================================
            // TLS Information
            // ============================================
            var tlsFeature = httpContext.Features.Get<ITlsConnectionFeature>();
            if (tlsFeature != null)
            {
                // Client certificate (if mTLS is used)
                if (tlsFeature.ClientCertificate != null)
                {
                    context.ProxyRequest.Headers.TryAddWithoutValidation(
                        "X-TLS-Client-Cert-Issuer",
                        tlsFeature.ClientCertificate.Issuer);

                    context.ProxyRequest.Headers.TryAddWithoutValidation(
                        "X-TLS-Client-Cert-Subject",
                        tlsFeature.ClientCertificate.Subject);
                }
            }

            // Extract TLS protocol and cipher from middleware-captured metadata
            // This data is captured by TlsMetadataMiddleware using SslStream
            if (httpContext.Items.TryGetValue("TLS.Protocol", out var tlsProtocol) && tlsProtocol != null)
            {
                context.ProxyRequest.Headers.TryAddWithoutValidation(
                    "X-TLS-Protocol",
                    tlsProtocol.ToString()!);
            }

            if (httpContext.Items.TryGetValue("TLS.CipherSuite", out var tlsCipher) && tlsCipher != null)
            {
                context.ProxyRequest.Headers.TryAddWithoutValidation(
                    "X-TLS-Cipher",
                    tlsCipher.ToString()!);
            }

            // ============================================
            // HTTP Protocol Information
            // ============================================
            var protocol = httpContext.Request.Protocol;
            if (!string.IsNullOrEmpty(protocol))
            {
                context.ProxyRequest.Headers.TryAddWithoutValidation(
                    "X-HTTP-Protocol", protocol);

                // HTTP/2 specific flag
                if (protocol.StartsWith("HTTP/2", StringComparison.OrdinalIgnoreCase))
                {
                    context.ProxyRequest.Headers.TryAddWithoutValidation(
                        "X-Is-HTTP2", "1");

                    // Note: HTTP/2 SETTINGS frame requires packet capture or Kestrel callback
                    // For production, integrate with:
                    // - HAProxy with Lua scripts
                    // - nginx with custom module
                    // - Kestrel with custom connection middleware
                }
            }

            // ============================================
            // TCP/IP Connection Information
            // ============================================
            var connection = httpContext.Connection;

            if (connection.RemoteIpAddress != null)
            {
                context.ProxyRequest.Headers.TryAddWithoutValidation(
                    "X-Client-IP", connection.RemoteIpAddress.ToString());

                context.ProxyRequest.Headers.TryAddWithoutValidation(
                    "X-Client-Port", connection.RemotePort.ToString());
            }

            if (connection.LocalIpAddress != null)
            {
                context.ProxyRequest.Headers.TryAddWithoutValidation(
                    "X-Local-IP", connection.LocalIpAddress.ToString());

                context.ProxyRequest.Headers.TryAddWithoutValidation(
                    "X-Local-Port", connection.LocalPort.ToString());
            }

            // Connection ID for request tracking
            context.ProxyRequest.Headers.TryAddWithoutValidation(
                "X-Connection-ID", connection.Id);

            // ============================================
            // Request Metadata
            // ============================================
            context.ProxyRequest.Headers.TryAddWithoutValidation(
                "X-Request-ID", httpContext.TraceIdentifier);

            // Timestamp for timing analysis
            context.ProxyRequest.Headers.TryAddWithoutValidation(
                "X-Request-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());

        }
        catch (Exception)
        {
            // Silently ignore errors - fingerprinting is best-effort
            // Don't let transform failures break proxying
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
///     Extension methods for registering TLS fingerprinting transforms in YARP.
/// </summary>
public static class TlsFingerprintingTransformExtensions
{
    /// <summary>
    ///     Add TLS/TCP/HTTP2 fingerprinting headers to all proxied requests.
    ///     Call this during YARP configuration to enable advanced bot detection.
    /// </summary>
    /// <example>
    ///     <code>
    ///     services.AddReverseProxy()
    ///         .LoadFromConfig(configuration.GetSection("ReverseProxy"))
    ///         .AddTransforms(builderContext =>
    ///         {
    ///             builderContext.AddTlsFingerprintingHeaders();
    ///         });
    ///     </code>
    /// </example>
    public static void AddTlsFingerprintingHeaders(this TransformBuilderContext context)
    {
        context.AddRequestTransform(transformContext =>
            new TlsFingerprintingTransform().ApplyAsync(transformContext));
    }

    /// <summary>
    ///     Add TLS fingerprinting headers with conditional logic.
    ///     Only adds headers if the predicate returns true.
    /// </summary>
    public static void AddTlsFingerprintingHeadersIf(
        this TransformBuilderContext context,
        Func<RequestTransformContext, bool> predicate)
    {
        context.AddRequestTransform(transformContext =>
        {
            if (predicate(transformContext))
            {
                return new TlsFingerprintingTransform().ApplyAsync(transformContext);
            }
            return ValueTask.CompletedTask;
        });
    }
}
