using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Mostlylucid.YarpGateway.Configuration;

/// <summary>
///     Kestrel TLS configuration that captures handshake metadata for bot detection.
///
///     This uses a custom ServerCertificateSelector callback to intercept the TLS handshake
///     and extract protocol version and cipher suite information.
/// </summary>
public static class KestrelTlsOptions
{
    /// <summary>
    ///     Configure Kestrel to capture TLS metadata during handshake.
    ///     Call this in Program.cs when configuring Kestrel.
    /// </summary>
    /// <param name="listenOptions">Kestrel listen options</param>
    /// <param name="certificate">Server certificate to use for TLS</param>
    /// <param name="logger">Logger for diagnostics</param>
    public static void UseHttpsWithTlsCapture(
        this ListenOptions listenOptions,
        X509Certificate2 certificate,
        ILogger logger)
    {
        listenOptions.UseHttps(httpsOptions =>
        {
            // Set the default certificate
            httpsOptions.ServerCertificate = certificate;

            // Configure TLS handshake callback to capture metadata
            httpsOptions.OnAuthenticate = (connectionContext, authOptions) =>
            {
                // Hook into the certificate selection to capture TLS info
                var originalSelector = authOptions.ServerCertificateSelectionCallback;

                authOptions.ServerCertificateSelectionCallback = (sender, hostName) =>
                {
                    // Get the certificate (either from original selector or default)
                    var cert = originalSelector?.Invoke(sender, hostName) ?? certificate;

                    // Try to extract TLS information from SslStream
                    if (sender is SslStream sslStream)
                    {
                        try
                        {
                            // Store metadata in connection context items
                            // We'll retrieve this later in middleware
                            if (connectionContext.Items.TryGetValue("Microsoft.AspNetCore.Http.HttpContext", out var httpContextObj) &&
                                httpContextObj is HttpContext httpContext)
                            {
                                // Extract TLS protocol
                                var protocol = sslStream.SslProtocol;
                                httpContext.Items["TLS.Protocol"] = GetProtocolName(protocol);

                                // Extract cipher suite
                                var cipher = sslStream.NegotiatedCipherSuite;
                                httpContext.Items["TLS.CipherSuite"] = cipher.ToString();

                                logger.LogTrace(
                                    "Captured TLS metadata - Protocol: {Protocol}, Cipher: {Cipher}",
                                    protocol,
                                    cipher);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to capture TLS metadata during handshake");
                        }
                    }

                    return cert;
                };
            };
        });
    }

    /// <summary>
    ///     Configure Kestrel to capture TLS metadata using ALPN callback (alternative approach).
    ///     This is called after TLS handshake completes, giving us access to negotiated parameters.
    /// </summary>
    public static void UseHttpsWithAlpnCapture(
        this ListenOptions listenOptions,
        X509Certificate2 certificate,
        ILogger logger)
    {
        listenOptions.UseHttps(httpsOptions =>
        {
            httpsOptions.ServerCertificate = certificate;

            // Use ALPN (Application-Layer Protocol Negotiation) callback
            // This fires AFTER TLS handshake, so we can read negotiated cipher
            httpsOptions.OnAuthenticate = (connectionContext, authOptions) =>
            {
                authOptions.ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    SslApplicationProtocol.Http2,
                    SslApplicationProtocol.Http11
                };

                // Store connection context for later retrieval in middleware
                authOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    // We can access SslStream here after handshake
                    if (sender is SslStream sslStream)
                    {
                        try
                        {
                            // Store in connection items for middleware to pick up
                            connectionContext.Items["TLS.Protocol"] = GetProtocolName(sslStream.SslProtocol);
                            connectionContext.Items["TLS.CipherSuite"] = sslStream.NegotiatedCipherSuite.ToString();
                            connectionContext.Items["TLS.ApplicationProtocol"] = sslStream.NegotiatedApplicationProtocol.ToString();

                            logger.LogTrace(
                                "TLS handshake complete - Protocol: {Protocol}, Cipher: {Cipher}, ALPN: {Alpn}",
                                sslStream.SslProtocol,
                                sslStream.NegotiatedCipherSuite,
                                sslStream.NegotiatedApplicationProtocol);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to capture TLS metadata in validation callback");
                        }
                    }

                    // Always accept (we're not validating client certs, just capturing metadata)
                    return true;
                };
            };
        });
    }

    private static string GetProtocolName(SslProtocols protocol)
    {
        return protocol switch
        {
            SslProtocols.Tls => "TLSv1.0",
            SslProtocols.Tls11 => "TLSv1.1",
            SslProtocols.Tls12 => "TLSv1.2",
            SslProtocols.Tls13 => "TLSv1.3",
            _ => protocol.ToString()
        };
    }
}

/// <summary>
///     Extension to retrieve TLS metadata from connection context in middleware.
/// </summary>
public static class ConnectionContextTlsExtensions
{
    /// <summary>
    ///     Copy TLS metadata from connection context to HttpContext items.
    ///     Call this in middleware to make TLS data available to transforms.
    /// </summary>
    public static void CopyTlsMetadataToHttpContext(this HttpContext httpContext)
    {
        var connectionFeature = httpContext.Features.Get<IConnectionItemsFeature>();
        if (connectionFeature?.Items == null)
            return;

        // Copy TLS metadata from connection items to HttpContext items
        if (connectionFeature.Items.TryGetValue("TLS.Protocol", out var protocol))
        {
            httpContext.Items["TLS.Protocol"] = protocol;
        }

        if (connectionFeature.Items.TryGetValue("TLS.CipherSuite", out var cipher))
        {
            httpContext.Items["TLS.CipherSuite"] = cipher;
        }

        if (connectionFeature.Items.TryGetValue("TLS.ApplicationProtocol", out var alpn))
        {
            httpContext.Items["TLS.ApplicationProtocol"] = alpn;
        }
    }
}
