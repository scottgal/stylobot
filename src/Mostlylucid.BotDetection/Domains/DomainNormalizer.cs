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

        var scope = Resolve(ctx.Request.Host.Host);
        ctx.Items[HttpContextItemKeys.RequestScope] = scope;
        ctx.Items[HttpContextItemKeys.Domain] = scope.Domain;
        ctx.Items[HttpContextItemKeys.Host] = scope.Host;
        return scope;
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