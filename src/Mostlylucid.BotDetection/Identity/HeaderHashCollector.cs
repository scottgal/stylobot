using System.Collections.Frozen;
using Mostlylucid.BotDetection.Dashboard;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Collects HMAC hashes of discriminatory HTTP headers per request.
///     Only headers with identity-discriminating value are hashed - junk headers
///     (Date, Content-Length, Host) are ignored.
///     The hashes are stored per session for retroactive stability analysis:
///     which headers are stable for THIS visitor across sessions?
/// </summary>
public sealed class HeaderHashCollector
{
    private readonly PiiHasher _hasher;

    /// <summary>
    ///     Headers worth hashing for identity resolution.
    ///     Selected for discriminatory value - each has a reasonable chance of being
    ///     stable for one visitor but different across visitors.
    /// </summary>
    private static readonly FrozenSet<string> CandidateHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Client hints - highly discriminating, browser-specific
        "sec-ch-ua",
        "sec-ch-ua-platform",
        "sec-ch-ua-mobile",
        "sec-ch-ua-full-version-list",
        "sec-ch-ua-arch",
        "sec-ch-ua-bitness",
        "sec-ch-ua-model",

        // Fetch metadata - reveals navigation context
        "sec-fetch-site",
        "sec-fetch-mode",
        "sec-fetch-dest",

        // Content negotiation - stable per browser/locale
        "accept",
        "accept-language",
        "accept-encoding",

        // Privacy signals - stable per user choice
        "dnt",
        "sec-gpc",

        // Connection behavior
        "connection",
        "upgrade-insecure-requests",
        "priority",

        // Ordering patterns - the ORDER of headers is itself discriminating
        // (captured separately via header ordering hash)
    }.ToFrozenSet();

    /// <summary>
    ///     Headers that are common junk - high entropy, low identity value.
    ///     Excluded even if they match candidate list.
    /// </summary>
    private static readonly FrozenSet<string> ExcludedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "content-type", "date", "cache-control",
        "pragma", "cookie", "set-cookie", "authorization", "x-forwarded-for",
        "x-real-ip", "x-request-id", "x-correlation-id", "traceparent",
        "x-sb-api-key", "if-none-match", "if-modified-since", "range",
        "referer", "origin" // Referer/Origin change per page, not per identity
    }.ToFrozenSet();

    public HeaderHashCollector(PiiHasher hasher)
    {
        _hasher = hasher;
    }

    /// <summary>
    ///     Collect HMAC hashes of discriminatory headers from the current request.
    ///     Returns a dictionary of header name → HMAC hash of value.
    ///     Also includes a special "header_order" key - the hash of the header name ordering,
    ///     which is itself a strong fingerprint signal (different HTTP stacks send headers
    ///     in different orders).
    /// </summary>
    public Dictionary<string, string> CollectHashes(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in request.Headers)
        {
            var name = header.Key.ToLowerInvariant();

            if (ExcludedHeaders.Contains(name)) continue;
            if (!CandidateHeaders.Contains(name)) continue;

            var value = header.Value.ToString();
            if (string.IsNullOrEmpty(value)) continue;

            hashes[name] = _hasher.ComputeSignature(value);
        }

        // Header ordering hash - the sequence of header names is a fingerprint
        // Different HTTP clients/browsers send headers in characteristic orders
        var headerNames = request.Headers.Keys
            .Where(k => !ExcludedHeaders.Contains(k))
            .Select(k => k.ToLowerInvariant())
            .ToList(); // Preserve original order, don't sort

        if (headerNames.Count > 0)
            hashes["_header_order"] = _hasher.ComputeSignature(string.Join("|", headerNames));

        return hashes;
    }
}