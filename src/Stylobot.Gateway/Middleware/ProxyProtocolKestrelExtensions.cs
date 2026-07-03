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

        // We bind endpoints explicitly below. Clear ONLY the URL list so it
        // doesn't double-bind. We must NOT touch Kestrel:Endpoints:* — defining
        // a named endpoint there (even with an empty Url) makes Kestrel demand a
        // Url and crash ("endpoint Http is missing the required 'Url'"). So when
        // PP is enabled the host config MUST NOT set any Kestrel:Endpoints:* keys;
        // the TLS cert is read from the dedicated Gateway:Tls:* keys instead.
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        // .NET base images default ASPNETCORE_HTTP_PORTS=8080 (config key
        // HTTP_PORTS after the ASPNETCORE_ prefix is stripped). Left set, Kestrel
        // would add a default :8080 endpoint that double-binds against our
        // explicit Listen below. Clear both ports settings.
        builder.WebHost.UseSetting("HTTP_PORTS", string.Empty);
        builder.WebHost.UseSetting("HTTPS_PORTS", string.Empty);

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
            // Dedicated keys (NOT under Kestrel:Endpoints — see note above).
            var certPath = cfg["Gateway:Tls:CertPath"];
            var keyPath = cfg["Gateway:Tls:KeyPath"];

            options.Listen(IPAddress.Any, httpPort, ApplyDefaults);

            var primary = LoadPemCert(certPath, keyPath);
            if (primary is not null)
            {
                // Additional SNI certs. The gateway is the TLS edge for more than
                // one domain family, and those families can live on different DNS
                // providers, so they can't share a single issued cert (e.g.
                // *.stylo.bot via Namecheap DNS-01 + *.stylobot.net via Cloudflare
                // DNS-01). Load each extra cert and pick per connection by SNI
                // hostname matched against the cert's SANs; fall back to primary.
                //   Gateway:Tls:AdditionalCerts:0:CertPath / :KeyPath, :1:…
                var extra = new List<X509Certificate2>();
                foreach (var c in cfg.GetSection("Gateway:Tls:AdditionalCerts").GetChildren())
                {
                    var loaded = LoadPemCert(c["CertPath"], c["KeyPath"]);
                    if (loaded is not null) extra.Add(loaded);
                }

                options.Listen(IPAddress.Any, httpsPort, lo =>
                {
                    ApplyDefaults(lo);   // PROXY parser FIRST
                    if (extra.Count == 0)
                    {
                        lo.UseHttps(primary);   // single-cert fast path (unchanged)
                    }
                    else
                    {
                        var all = new List<X509Certificate2>(extra.Count + 1) { primary };
                        all.AddRange(extra);
                        // TLS AFTER, selecting the cert by SNI so *.stylo.bot and
                        // *.stylobot.net each get their own valid chain.
                        lo.UseHttps(https =>
                            https.ServerCertificateSelector = (_, sni) => SelectCertBySni(sni, all, primary));
                    }
                });
                Log.Information(
                    "PROXY protocol ENABLED — code-bound endpoints http:{HttpPort} https:{HttpsPort} (trustAll={TrustAll}, trustedCidrs={Count}, tlsCerts={Certs})",
                    httpPort, httpsPort, trustAll, trusted.Count, extra.Count + 1);
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

    // Load a PEM cert+key pair, round-tripping through PKCS#12 so the private key
    // is usable for the TLS handshake on the Linux container runtime. Returns null
    // when the path is unset or the file is missing (caller logs / falls back).
    private static X509Certificate2? LoadPemCert(string? certPath, string? keyPath)
    {
        if (string.IsNullOrEmpty(certPath) || string.IsNullOrEmpty(keyPath) || !File.Exists(certPath))
            return null;
        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }

    // Pick the cert whose SANs cover the requested SNI host; fall back to primary
    // (also covers the no-SNI case, e.g. an IP-address or bare-hostname handshake).
    private static X509Certificate2 SelectCertBySni(string? sni, List<X509Certificate2> certs, X509Certificate2 fallback)
    {
        if (string.IsNullOrEmpty(sni)) return fallback;
        foreach (var cert in certs)
            if (CertCoversHost(cert, sni)) return cert;
        return fallback;
    }

    private static bool CertCoversHost(X509Certificate2 cert, string host)
    {
        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue; // subjectAltName
            X509SubjectAlternativeNameExtension san;
            try { san = new X509SubjectAlternativeNameExtension(ext.RawData); }
            catch { continue; }
            foreach (var dns in san.EnumerateDnsNames())
            {
                if (dns.StartsWith("*.", StringComparison.Ordinal))
                {
                    // Wildcard matches exactly one label: *.stylobot.net covers
                    // a.stylobot.net but not b.a.stylobot.net nor the apex.
                    var suffix = dns.AsSpan(1); // ".stylobot.net"
                    var dot = host.IndexOf('.');
                    if (dot > 0 && host.AsSpan(dot).Equals(suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (dns.Equals(host, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
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