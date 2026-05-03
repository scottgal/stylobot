using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Detects UA rotation by correlating requests that have different PrimarySignatures
///     but identical non-UA header profiles. When a bot rotates User-Agent strings,
///     everything ELSE stays the same - Accept-Encoding, Accept-Language, connection
///     behavior, Sec-CH-UA ordering. This detector catches that pattern.
///
///     Works by hashing the "header fingerprint" (all headers EXCEPT User-Agent) and
///     tracking how many distinct PrimarySignatures share the same header fingerprint
///     from the same IP. Multiple signatures with identical header profiles from one IP
///     = UA rotation.
///
///     This works even in loopback (no TLS/TCP needed) because it's pure HTTP header analysis.
/// </summary>
public class HeaderCorrelationContributor : ConfiguredContributorBase
{
    private readonly ILogger<HeaderCorrelationContributor> _logger;
    private readonly IMemoryCache _cache;

    private const string CachePrefix = "headercorr:";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(30);

    private int MinSignaturesForRotation => GetParam("min_signatures_for_rotation", 3);
    private double RotationBotConfidence => GetParam("rotation_bot_confidence", 0.5);
    private double RotationBotWeight => GetParam("rotation_bot_weight", 1.8);

    public HeaderCorrelationContributor(
        ILogger<HeaderCorrelationContributor> logger,
        IDetectorConfigProvider configProvider,
        IMemoryCache cache) : base(configProvider)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "HeaderCorrelation";
    protected override string ManifestName => Name; // YAML name is "HeaderCorrelation", not "HeaderCorrelationContributor"
    public override int Priority => 21;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => [];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
        var clientIp = state.GetSignal<string>(SignalKeys.ClientIp) ?? "unknown";

        if (string.IsNullOrEmpty(signature))
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        // Build a header fingerprint from everything EXCEPT User-Agent
        var headerFingerprint = BuildHeaderFingerprint(state.HttpContext.Request);

        if (string.IsNullOrEmpty(headerFingerprint))
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        // Track: for this IP + header fingerprint combo, how many distinct signatures?
        var key = $"{CachePrefix}{clientIp}:{headerFingerprint}";
        var sigSet = _cache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = CacheExpiration;
            return new ConcurrentDictionary<string, byte>();
        })!;

        sigSet.TryAdd(signature, 0);

        var distinctSignatures = sigSet.Count;

        state.WriteSignal("header_correlation.distinct_signatures", distinctSignatures);
        state.WriteSignal("header_correlation.header_fingerprint", headerFingerprint[..Math.Min(8, headerFingerprint.Length)]);

        if (distinctSignatures >= MinSignaturesForRotation)
        {
            _logger.LogInformation(
                "UA rotation detected: {Count} distinct signatures sharing header fingerprint from IP {Ip}",
                distinctSignatures, clientIp[..Math.Min(12, clientIp.Length)]);

            // Scale confidence with number of rotations
            var scaledConfidence = Math.Min(0.9,
                RotationBotConfidence + (distinctSignatures - MinSignaturesForRotation) * 0.1);

            contributions.Add(BotContribution(
                "UaRotation",
                $"UA rotation: {distinctSignatures} different User-Agents with identical header profile from same IP",
                confidenceOverride: scaledConfidence,
                weightMultiplier: RotationBotWeight,
                botType: BotType.Scraper.ToString()));
        }
        else if (distinctSignatures == 2)
        {
            // Two signatures - suspicious but not conclusive (could be browser update)
            contributions.Add(NeutralContribution(
                "HeaderCorrelation",
                $"2 signatures with similar headers from same IP (monitoring)"));
        }

        if (contributions.Count == 0)
            contributions.Add(NeutralContribution("HeaderCorrelation", "Single signature per header profile"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    /// <summary>
    ///     Build a fingerprint from all headers EXCEPT User-Agent.
    ///     The fingerprint is a hash of the sorted header names + values.
    ///     When a bot rotates UA but keeps everything else the same,
    ///     this fingerprint is identical across rotations.
    /// </summary>
    private static string BuildHeaderFingerprint(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        // Headers that differentiate real visitors but stay constant during rotation
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

        // Simple hash - we don't need crypto here, just deduplication
        return combined.GetHashCode().ToString("X8");
    }
}