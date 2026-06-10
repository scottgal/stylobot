using System.Net;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Canonical resolver for the <see cref="RateLimitKey"/> enum.
///     Single source of truth for "what identity key does this request
///     bill against?" -- consumed by <see cref="RateLimitActionPolicy"/>
///     (rate-limit bucket key) and <see cref="StickyDenyActionPolicy"/>
///     (violation-counter key) and any future policy that needs the same
///     derivation.
///     <para>
///     Lives here rather than inside either policy because both previously
///     held byte-for-byte duplicate copies of the switch + helpers. A
///     subnet/asn semantics change to one had no mechanical guarantee of
///     reaching the other. Now there's one place to change.
///     </para>
/// </summary>
internal static class RateLimitKeyResolver
{
    /// <summary>
    ///     Returns the bucket / counter key for the current request under
    ///     the given <paramref name="keyBy"/> mode. Returns <c>null</c>
    ///     when the source signal isn't available (callers treat <c>null</c>
    ///     as "fail open / no bucket / no counter" rather than routing
    ///     every blank-evidence visitor through one global key).
    /// </summary>
    public static string? Resolve(HttpContext context, AggregatedEvidence evidence, RateLimitKey keyBy) => keyBy switch
    {
        RateLimitKey.Signature => GetSignature(evidence),
        RateLimitKey.Ip => context.Connection.RemoteIpAddress?.ToString(),
        RateLimitKey.Subnet => ComputeSubnetKey(context.Connection.RemoteIpAddress),
        RateLimitKey.Asn => GetAsn(evidence),
        RateLimitKey.AsnAndSignature => ComposeAsnAndSignature(evidence),
        _ => null,
    };

    public static string? GetSignature(AggregatedEvidence evidence)
        => SignatureLookup.Primary(evidence);

    public static string? GetAsn(AggregatedEvidence evidence)
    {
        if (!evidence.Signals.TryGetValue(SignalKeys.IpAsn, out var asnObj) || asnObj is null)
            return null;
        var asn = asnObj.ToString();
        return string.IsNullOrWhiteSpace(asn) ? null : asn;
    }

    /// <summary>
    ///     Composite "this signature within this ASN" key. Requires both
    ///     halves to be present -- a missing component returns null so the
    ///     caller fails open rather than collapsing every blank-evidence
    ///     visitor into one global bucket.
    /// </summary>
    public static string? ComposeAsnAndSignature(AggregatedEvidence evidence)
    {
        var asn = GetAsn(evidence);
        var sig = GetSignature(evidence);
        if (asn is null || sig is null) return null;
        return $"AS{asn}:{sig}";
    }

    /// <summary>
    ///     Truncate an IP to its rate-limit-bucket prefix: <c>/24</c> for
    ///     IPv4, <c>/48</c> for IPv6 (HAProxy / Cloudflare conventions).
    ///     Returns the canonical string form (<c>"203.0.113.0/24"</c>,
    ///     <c>"2001:db8:1::/48"</c>) so two addresses inside the same
    ///     prefix hash to the same bucket key.
    /// </summary>
    public static string? ComputeSubnetKey(IPAddress? ip)
    {
        if (ip is null) return null;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        }
        if (bytes.Length == 16)
        {
            // Zero everything past the first 6 bytes (the /48 prefix).
            var truncated = new byte[16];
            Array.Copy(bytes, 0, truncated, 0, 6);
            return new IPAddress(truncated).ToString() + "/48";
        }
        // Unknown address family (e.g. IPv6 link-local with zone id stripped
        // to non-standard length). Fail open.
        return null;
    }
}
