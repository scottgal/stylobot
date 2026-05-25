using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     Produces the set of <see cref="RateLimitSubject"/> values that apply
///     to a request, given the request's <see cref="HttpContext"/> and the
///     detection outcome. The rule matcher (phase 5) then intersects the
///     request's subject set against each rule's declared subjects -- a
///     rule fires when ALL its subjects are present in the request set.
///     <para>
///     The resolver is read-only and pure: same inputs, same outputs. State
///     (label cache, region map) is injected so behaviour is testable and
///     swappable per deployment.
///     </para>
/// </summary>
public sealed class RateLimitSubjectResolver
{
    private readonly IPinnedLabelLookup? _labels;
    private readonly IRegionMap? _regionMap;

    public RateLimitSubjectResolver(
        IPinnedLabelLookup? labels = null,
        IRegionMap? regionMap = null)
    {
        _labels = labels;
        _regionMap = regionMap;
    }

    /// <summary>
    ///     Resolve every applicable subject. Empty fields are skipped -- a
    ///     request with no detection.BotType produces no
    ///     <see cref="RateLimitSubjectKind.BotType"/> subject, which means rules
    ///     that require one cannot match it (correct behaviour: don't accidentally
    ///     bucket low-information rows into the "Unknown" tier).
    /// </summary>
    public IReadOnlyList<RateLimitSubject> Resolve(
        HttpContext context,
        DetectionFacts detection)
    {
        var subjects = new List<RateLimitSubject>(capacity: 7);

        if (!string.IsNullOrEmpty(detection.PrimarySignature))
            subjects.Add(new(RateLimitSubjectKind.Fingerprint, detection.PrimarySignature));

        if (!string.IsNullOrEmpty(detection.BotType))
            subjects.Add(new(RateLimitSubjectKind.BotType, detection.BotType));

        if (!string.IsNullOrEmpty(detection.BotName))
            subjects.Add(new(RateLimitSubjectKind.BotName, detection.BotName));

        if (!string.IsNullOrEmpty(detection.CountryCode))
            subjects.Add(new(RateLimitSubjectKind.Country, detection.CountryCode));

        if (!string.IsNullOrEmpty(detection.CountryCode))
        {
            var region = _regionMap?.GetRegion(detection.CountryCode);
            if (!string.IsNullOrEmpty(region))
                subjects.Add(new(RateLimitSubjectKind.Region, region));
        }

        if (!string.IsNullOrEmpty(detection.PrimarySignature) && _labels is not null)
        {
            var label = _labels.GetLabel(detection.PrimarySignature);
            if (!string.IsNullOrEmpty(label))
                subjects.Add(new(RateLimitSubjectKind.PinnedLabel, label));
        }

        var subnet = HashedSubnetKey(context.Connection.RemoteIpAddress);
        if (!string.IsNullOrEmpty(subnet))
            subjects.Add(new(RateLimitSubjectKind.IpSubnet, subnet));

        return subjects;
    }

    /// <summary>
    ///     SHA256 of the /24 (IPv4) or /48 (IPv6) prefix string. Not a security
    ///     boundary -- the bucket key is process-local for FOSS, a Redis key
    ///     for commercial; collisions are acceptable. The hash is the cheapest
    ///     stable transform that doesn't carry the raw IP into the rate-limit
    ///     subsystem's keyspace.
    /// </summary>
    private static string? HashedSubnetKey(IPAddress? remote)
    {
        if (remote is null) return null;
        var bytes = remote.GetAddressBytes();

        string prefix;
        if (bytes.Length == 4)
        {
            // IPv4 /24
            prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        }
        else if (bytes.Length == 16)
        {
            // IPv6 /48 -- first three 16-bit groups
            prefix = string.Concat(
                bytes[0].ToString("x2"), bytes[1].ToString("x2"), ":",
                bytes[2].ToString("x2"), bytes[3].ToString("x2"), ":",
                bytes[4].ToString("x2"), bytes[5].ToString("x2"), "::/48");
        }
        else
        {
            return null;
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(prefix), hash);
        return Convert.ToHexString(hash[..8]); // 16-hex-char prefix -- plenty for distinct buckets
    }
}

/// <summary>
///     Minimal detection-result fields the subject resolver consumes. Defined
///     here rather than referencing <c>BotDetectionResult</c> directly so the
///     resolver is testable without the rest of the detection pipeline and
///     the test project doesn't need the full BotDetectionResult wiring.
/// </summary>
public sealed record DetectionFacts(
    string? PrimarySignature,
    string? BotType,
    string? BotName,
    string? CountryCode);

/// <summary>
///     Country (ISO-2) → region (EU / APAC / NA / ...) mapping. Config-driven;
///     the FOSS package ships no default so operators see "no Region subject
///     fired" until they declare a map. Implementations should be O(1).
/// </summary>
public interface IRegionMap
{
    string? GetRegion(string countryCode);
}
