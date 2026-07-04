using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that detects UA rotation by
///     correlating requests that have different <c>PrimarySignatures</c> but
///     identical non-UA header profiles. When a bot rotates User-Agent strings,
///     everything ELSE stays the same -- Accept-Encoding, Accept-Language,
///     connection behavior, Sec-CH-UA ordering. This atom catches that pattern.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>HeaderCorrelationContributor</c>. Hashes the "header fingerprint"
///         (all headers EXCEPT User-Agent) and tracks how many distinct
///         PrimarySignatures share the same header fingerprint from the same
///         IP. Multiple signatures with identical header profiles from one IP
///         = UA rotation.
///     </para>
///     <para>
///         Works even in loopback (no TLS/TCP needed) because it's pure HTTP
///         header analysis. Cross-request state lives in <see cref="IMemoryCache"/>
///         with a 30-minute sliding expiration. Priority 21 matches the legacy
///         Wave-1 slot.
///     </para>
/// </remarks>
public sealed class HeaderCorrelationAtom : DetectorAtomBase
{
    private readonly ILogger<HeaderCorrelationAtom> _logger;
    private readonly IMemoryCache _cache;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private const string CachePrefix = "headercorr:";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(30);

    public HeaderCorrelationAtom(
        ILogger<HeaderCorrelationAtom> logger,
        IDetectorConfigProvider configProvider,
        IMemoryCache cache,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "HeaderCorrelation", category: "HeaderCorrelation")
    {
        _logger = logger;
        _cache = cache;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 21;

    private int MinSignaturesForRotation =>
        _configProvider.GetParameter(Name, "min_signatures_for_rotation", 3);

    private double RotationBotConfidence =>
        _configProvider.GetParameter(Name, "rotation_bot_confidence", 0.5);

    private double RotationBotWeight =>
        _configProvider.GetParameter(Name, "rotation_bot_weight", 1.8);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
            return Task.FromResult(None());

        // Read Model-2 hints from the sink -- signature + client-ip carried
        // by upstream sensor atoms as "signature.primary:value" /
        // "client_ip:value".
        var signature = sink.ReadHint(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
            return Task.FromResult(None());

        var clientIp = sink.ReadHint(SignalKeys.ClientIp) ?? "unknown";

        var headerFingerprint = BuildHeaderFingerprint(context.Request);
        if (string.IsNullOrEmpty(headerFingerprint))
            return Task.FromResult(None());

        // Cross-request state in IMemoryCache -- distinct signatures per
        // (IP, headerFingerprint) tuple with sliding expiration.
        var key = $"{CachePrefix}{clientIp}:{headerFingerprint}";
        var sigSet = _cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = CacheExpiration;
            return new ConcurrentDictionary<string, byte>();
        })!;
        sigSet.TryAdd(signature, 0);
        var distinctSignatures = sigSet.Count;

        sink.Raise($"{SignalKeys.HeaderCorrelationDistinctSignatures}:{distinctSignatures}", sessionId);
        sink.Raise(
            $"{SignalKeys.HeaderCorrelationHeaderFingerprint}:{headerFingerprint[..Math.Min(8, headerFingerprint.Length)]}",
            sessionId);

        if (distinctSignatures >= MinSignaturesForRotation)
        {
            _logger.LogInformation(
                "UA rotation detected: {Count} distinct signatures sharing header fingerprint from IP {Ip}",
                distinctSignatures, clientIp[..Math.Min(12, clientIp.Length)]);

            // Scale confidence with number of rotations
            var scaledConfidence = Math.Min(0.9,
                RotationBotConfidence + (distinctSignatures - MinSignaturesForRotation) * 0.1);

            return Task.FromResult(Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = "UaRotation",
                ConfidenceDelta = scaledConfidence,
                Weight = RotationBotWeight,
                Reason = $"UA rotation: {distinctSignatures} different User-Agents with identical header profile from same IP",
                BotType = BotType.Scraper.ToString()
            }));
        }

        if (distinctSignatures == 2)
        {
            // Two signatures -- suspicious but not conclusive (could be browser update)
            return Task.FromResult(Single(DetectionContribution.Info(
                Name,
                "HeaderCorrelation",
                "2 signatures with similar headers from same IP (monitoring)")));
        }

        return Task.FromResult(Single(DetectionContribution.Info(
            Name,
            "HeaderCorrelation",
            "Single signature per header profile")));
    }

    /// <summary>
    ///     Build a fingerprint from all headers EXCEPT User-Agent.
    ///     The fingerprint is a hash of the sorted header names + values.
    ///     When a bot rotates UA but keeps everything else the same,
    ///     this fingerprint is identical across rotations.
    /// </summary>
    private static string BuildHeaderFingerprint(HttpRequest request)
    {
        var discriminators = new List<string>();

        foreach (var header in request.Headers)
        {
            var name = header.Key.ToLowerInvariant();

            // Skip UA (that's what's being rotated) and volatile headers
            if (name is "user-agent" or "host" or "content-length" or "date"
                or "cookie" or "authorization" or "referer" or "origin"
                or "x-forwarded-for" or "x-real-ip" or "x-request-id"
                or "x-correlation-id" or "traceparent" or "x-sb-api-key"
                or "if-none-match" or "if-modified-since" or "cache-control")
                continue;

            discriminators.Add($"{name}={header.Value}");
        }

        if (discriminators.Count < 2) return string.Empty;

        discriminators.Sort(StringComparer.Ordinal);
        var combined = string.Join("|", discriminators);

        // Simple hash -- we don't need crypto here, just deduplication
        return combined.GetHashCode().ToString("X8");
    }

}
