using System.Net;
using System.Net.Sockets;

namespace Mostlylucid.BotDetection.Llm.Tunnel;

/// <summary>
///     Validates operator/key-supplied LLM endpoint URLs before any HttpClient is
///     pointed at them. Connection keys are importable artifacts — a key with a
///     hostile <c>TunnelUrl</c> would otherwise turn the controller into an SSRF
///     proxy. Loopback and RFC1918 ranges stay allowed (a LAN GPU box is the
///     product's primary topology); the link-local range is rejected because it
///     is never a legitimate tunnel target and covers the cloud metadata
///     endpoint (169.254.169.254 / fe80::).
/// </summary>
public static class LlmEndpointUrlValidator
{
    /// <summary>
    ///     Throws <see cref="FormatException"/> when <paramref name="url"/> is not an
    ///     absolute http/https URL or points at a link-local address.
    /// </summary>
    public static Uri Validate(string url, string description)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new FormatException($"{description} is empty.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new FormatException($"{description} is not a valid absolute URL: '{url}'");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException(
                $"{description} must use http or https, got scheme '{uri.Scheme}'.");

        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IsLinkLocal(ip))
                throw new FormatException(
                    $"{description} points at a link-local address ('{uri.Host}'), which is never a valid tunnel target.");
        }
        else
        {
            // Best-effort: reject a hostname that demonstrably resolves to a
            // link-local address (nip.io-style bypass of the literal check).
            // A resolution FAILURE is not rejected — a tunnel hostname may
            // legitimately be unresolvable at import time (offline import,
            // tunnel not yet up, unit tests), the connection itself fails
            // safely later, and an attacker who controls DNS answers can
            // defeat any resolve-time check via rebinding regardless.
            IPAddress[] resolved;
            try { resolved = Dns.GetHostAddresses(uri.Host); }
            catch { resolved = []; }

            foreach (var addr in resolved)
                if (IsLinkLocal(addr))
                    throw new FormatException(
                        $"{description} resolves to a link-local address ('{uri.Host}'), which is never a valid tunnel target.");
        }
        return uri;
    }

    private static bool IsLinkLocal(IPAddress ip)
    {
        if (ip.IsIPv6LinkLocal) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
