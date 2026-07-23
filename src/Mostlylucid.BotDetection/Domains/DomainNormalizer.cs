using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Domains;

/// <summary>
/// Byte-identical domain / host normalization used by every writer. Domain is the
/// eTLD+1 (registrable) form; host is the full lowercased port-stripped Host header.
/// Backed by the embedded Public Suffix List.
/// </summary>
public sealed class DomainNormalizer
{
    private readonly DomainNormalizerOptions _opts;
    private readonly PublicSuffixList _psl;
    private readonly HashSet<string> _hostingExceptions;

    public DomainNormalizer(IOptions<DomainNormalizerOptions> opts, PublicSuffixList psl)
    {
        _opts = opts.Value;
        _psl = psl;
        _hostingExceptions = new HashSet<string>(_opts.HostingProviderExceptions, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Lowercased, port-stripped host. "unknown" on null/empty.</summary>
    public string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return _opts.UnknownTag;

        var stripped = StripPort(host).ToLowerInvariant().TrimEnd('.');
        return stripped.Length == 0 ? _opts.UnknownTag : stripped;
    }

    /// <summary>eTLD+1 for the host, or "local" for private-range / loopback, or "unknown" on null/empty.</summary>
    public string NormalizeDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return _opts.UnknownTag;

        var stripped = StripPort(host).ToLowerInvariant().TrimEnd('.');
        if (stripped.Length == 0) return _opts.UnknownTag;

        if (IsLoopbackOrPrivate(stripped)) return _opts.LocalTag;

        // Hosting-provider exception: the full label UNDER the provider is registrable.
        foreach (var provider in _hostingExceptions)
        {
            if (stripped.EndsWith("." + provider, StringComparison.OrdinalIgnoreCase))
            {
                var prefix = stripped[..^(provider.Length + 1)];
                var lastLabel = prefix.LastIndexOf('.');
                var providerLabel = (lastLabel >= 0 ? prefix[(lastLabel + 1)..] : prefix);
                return providerLabel + "." + provider;
            }
        }

        return _psl.GetRegistrableDomain(stripped);
    }

    /// <summary>Resolve both in one call.</summary>
    public RequestScope Resolve(string? host)
        => new(NormalizeDomain(host), NormalizeHost(host));

    /// <summary>Resolve from an ASP.NET Core HttpContext + cache on Items.</summary>
    public RequestScope Resolve(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(HttpContextItemKeys.RequestScope, out var cached) && cached is RequestScope existing)
            return existing;

        // Domain attribution prefers the TLS-edge-validated SNI (the domain the gateway actually
        // served a cert for on this connection) over the client-supplied Host header. The gateway
        // only completes the handshake for domains it holds a cert for, so a validated SNI is
        // trustworthy where a Host header is not (a client can send any Host).
        //   - served SNI present  -> authoritative domain source.
        //   - gateway evaluated but NOT served -> "unknown" + flag the tls.sni_not_served signal
        //     (default-cert / no-SNI / spoofed-domain tell). Never fall through to the Host header.
        //   - no gateway TLS evaluation at all (non-gateway topology: direct AddBotDetection,
        //     sidecar, the demo) -> fall back to the Host header, the only source available there.
        var (servedSni, evaluated) = ReadValidatedSni(ctx);
        RequestScope scope;
        if (servedSni is not null)
        {
            scope = Resolve(servedSni);
        }
        else if (evaluated)
        {
            scope = RequestScope.Unknown;
            ctx.Items[HttpContextItemKeys.TlsSniNotServed] = true;
        }
        else
        {
            scope = Resolve(ctx.Request.Host.Host);
        }

        ctx.Items[HttpContextItemKeys.RequestScope] = scope;
        ctx.Items[HttpContextItemKeys.Domain] = scope.Domain;
        ctx.Items[HttpContextItemKeys.Host] = scope.Host;
        return scope;
    }

    /// <summary>
    ///     Reads the gateway-recorded TLS SNI state from the connection items.
    ///     <paramref name="servedSni"/> is the normalized SNI the gateway served a cert for (null
    ///     when none); <paramref name="evaluated"/> is true when the gateway processed this
    ///     connection's TLS at all (served or not). Both absent on non-gateway topologies.
    /// </summary>
    private static (string? servedSni, bool evaluated) ReadValidatedSni(HttpContext ctx)
    {
        var items = ctx.Features.Get<Microsoft.AspNetCore.Connections.Features.IConnectionItemsFeature>()?.Items;
        if (items is null) return (null, false);
        var evaluated = items.TryGetValue(TlsConnectionKeys.SniEvaluated, out var ev) && ev is true;
        var served = items.TryGetValue(TlsConnectionKeys.ValidatedSni, out var sni) ? sni as string : null;
        return (string.IsNullOrEmpty(served) ? null : served, evaluated);
    }

    private static string StripPort(string host)
    {
        // IPv6 bracketed form.
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close > 0) return host[1..close];
        }
        var colon = host.LastIndexOf(':');
        return colon > 0 && !host.Contains("::") ? host[..colon] : host;
    }

    private static bool IsLoopbackOrPrivate(string host)
    {
        if (host == "localhost") return true;
        if (!IPAddress.TryParse(host, out var addr)) return false;
        if (IPAddress.IsLoopback(addr)) return true;

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = addr.GetAddressBytes();
            if (bytes[0] == 10) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        }

        return false;
    }
}