using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Stylobot.Gateway.Middleware;

/// <summary>
///     Wires PROXY-protocol-aware Kestrel endpoints for any gateway host (FOSS
///     Stylobot.Gateway AND the commercial GatewayHost share this single path).
///
///     When <c>Network:ProxyProtocol:Enabled</c> is true the gateway binds its
///     http + https endpoints EXPLICITLY in code so the PROXY-header parser is
///     guaranteed to run before TLS on the connection (a config-bound HTTPS
///     endpoint otherwise runs the TLS middleware first, which then chokes on
///     the PROXY-header bytes). The real client IP rides as an L4 header through
///     a TCP proxy / tunnel that can't add X-Forwarded-For — JA3 stays intact
///     because the TLS bytes are never decrypted by the edge.
///
///     Config:
///       Network:ProxyProtocol:Enabled        (bool, default false)
///       Network:ProxyProtocol:TrustedProxies (CSV of CIDRs that may send headers)
///       Network:ProxyProtocol:TrustAll       (bool — accept from any peer; only
///                                              safe when the listener isn't
///                                              publicly reachable)
///     Ports + cert reuse the keys the gateway already sets:
///       Gateway:HttpPort | GATEWAY_HTTP_PORT  (default 8080)
///       Gateway:HttpsPort                     (default 8443)
///       Kestrel:Endpoints:Https:Certificate:Path / :KeyPath  (PEM)
/// </summary>
public static class ProxyProtocolKestrelExtensions
{
    public static WebApplicationBuilder AddProxyProtocolGatewayEndpoints(this WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var enabled = cfg.GetValue("Network:ProxyProtocol:Enabled", false);
        if (!enabled) return builder; // no-op — existing URL/config binding untouched

        var trustAll = cfg.GetValue("Network:ProxyProtocol:TrustAll", false);
        var trusted = ParseTrustedCidrs(cfg["Network:ProxyProtocol:TrustedProxies"]);

        // We bind endpoints explicitly below; clear the URL/Kestrel-config
        // auto-bind sources so they don't double-bind the same ports.
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseSetting("Kestrel:Endpoints:Https:Url", string.Empty);
        builder.WebHost.UseSetting("Kestrel:Endpoints:Http:Url", string.Empty);

        void ApplyDefaults(ListenOptions lo)
        {
            lo.Protocols = HttpProtocols.Http1AndHttp2;
            lo.Use(next =>
            {
                var mw = new ProxyProtocolConnectionMiddleware(
                    next, trusted, trustAll, msg => Log.Debug("{Msg}", msg));
                return mw.OnConnectionAsync;
            });
        }

        builder.WebHost.ConfigureKestrel(options =>
        {
            var httpPort = cfg.GetValue("Gateway:HttpPort", cfg.GetValue("GATEWAY_HTTP_PORT", 8080));
            var httpsPort = cfg.GetValue("Gateway:HttpsPort", 8443);
            var certPath = cfg["Kestrel:Endpoints:Https:Certificate:Path"];
            var keyPath = cfg["Kestrel:Endpoints:Https:Certificate:KeyPath"];

            options.Listen(IPAddress.Any, httpPort, ApplyDefaults);

            if (!string.IsNullOrEmpty(certPath) && !string.IsNullOrEmpty(keyPath) && File.Exists(certPath))
            {
                var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                // PEM → PKCS#12 round-trip so the private key is usable for the
                // TLS handshake on the Linux container runtime.
                var cert = X509CertificateLoader.LoadPkcs12(
                    pem.Export(X509ContentType.Pkcs12), password: null);

                options.Listen(IPAddress.Any, httpsPort, lo =>
                {
                    ApplyDefaults(lo);   // PROXY parser FIRST
                    lo.UseHttps(cert);   // TLS AFTER
                });
                Log.Information(
                    "PROXY protocol ENABLED — code-bound endpoints http:{HttpPort} https:{HttpsPort} (trustAll={TrustAll}, trustedCidrs={Count})",
                    httpPort, httpsPort, trustAll, trusted.Count);
            }
            else
            {
                Log.Warning(
                    "PROXY protocol enabled but HTTPS cert not found at '{CertPath}' — only HTTP:{HttpPort} bound with PP",
                    certPath, httpPort);
            }
        });

        return builder;
    }

    private static IReadOnlyList<IPNetwork> ParseTrustedCidrs(string? csv)
    {
        var nets = new List<IPNetwork>();
        if (string.IsNullOrWhiteSpace(csv)) return nets;

        foreach (var entry in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var prefix) && int.TryParse(parts[1], out var len))
            {
                try { nets.Add(new IPNetwork(prefix, len)); } catch { /* skip malformed */ }
            }
            else if (parts.Length == 1 && IPAddress.TryParse(parts[0], out var single))
            {
                var bits = single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
                nets.Add(new IPNetwork(single, bits));
            }
        }
        return nets;
    }
}