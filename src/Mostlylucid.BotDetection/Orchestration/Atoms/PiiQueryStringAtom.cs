using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Privacy;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Boundary SensorAtom (per Taxonomy.md) — extracts UTM / click-ID and PII
///     signals from the request query string BEFORE any sanitizer strips them.
///     Detected UTM signals feed downstream click-fraud detection; detected PII
///     signals feed compliance dashboards. All values are hashed at the
///     boundary via <see cref="PiiHasher"/> so zero PII is stored.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for the legacy
///         <c>PiiQueryStringContributor</c>. Reads the raw query string once
///         via <see cref="IHttpContextAccessor"/> (the atom is a boundary
///         sensor — the operator directive is "no adapters"; sensors close
///         to HTTP can take the accessor because they extract from raw
///         request into signals, and downstream classifier atoms then read
///         only from the sink).
///     </para>
///     <para>
///         Emits the same signal names the legacy contributor wrote to
///         <c>BlackboardState</c> so downstream consumers that read
///         <see cref="SignalKeys.UtmPresent"/>, <see cref="SignalKeys.PrivacyQueryPiiDetected"/>,
///         etc. off the sink see the same surface.
///     </para>
///     <para>
///         Priority 8 matches the legacy contributor's Wave-0 slot.
///     </para>
/// </remarks>
public sealed class PiiQueryStringAtom : DetectorAtomBase
{
    private readonly ILogger<PiiQueryStringAtom> _logger;
    private readonly PiiHasher _hasher;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PiiQueryStringAtom(
        ILogger<PiiQueryStringAtom> logger,
        PiiHasher hasher,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "PiiQueryString", category: "Privacy")
    {
        _logger = logger;
        _hasher = hasher;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 8;

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
            return Task.FromResult(None());

        var queryString = context.Request.QueryString.Value;
        if (string.IsNullOrEmpty(queryString))
            return Task.FromResult(None());

        var referer = context.Request.Headers.Referer.ToString();

        // --- UTM / click-ID detection (before PII stripping) ---
        var adTraffic = QueryStringSanitizer.DetectAdTrafficParams(
            queryString,
            string.IsNullOrEmpty(referer) ? null : referer,
            _hasher.GetKey());

        if (adTraffic.UtmPresent)
        {
            // Bare sink.Raise(key, sessionId) only records the key as a post-hoc merged-dict
            // signal; it does NOT populate the same-request hint cache ReadHint/ReadBoolHint
            // read (only colon-encoded "key:value" raises do -- confirmed via direct
            // instrumentation, 2026-08-17). ClickFraudAtom reads five of these six as
            // ReadBoolHint gates, including its own top-level "is this even paid traffic"
            // OR-trigger -- the bare form left its entire UTM/click-ID detection arm
            // permanently dead regardless of what any request's query string carried.
            sink.Raise($"{SignalKeys.UtmPresent}:true", sessionId);
            if (!string.IsNullOrEmpty(adTraffic.SourcePlatform))
                sink.Raise($"{SignalKeys.UtmSourcePlatform}:{adTraffic.SourcePlatform}", sessionId);
            if (adTraffic.HasGclid) sink.Raise($"{SignalKeys.UtmHasGclid}:true", sessionId);
            if (adTraffic.HasFbclid) sink.Raise($"{SignalKeys.UtmHasFbclid}:true", sessionId);
            if (adTraffic.HasMsclkid) sink.Raise($"{SignalKeys.UtmHasMsclkid}:true", sessionId);
            if (adTraffic.HasTtclid) sink.Raise($"{SignalKeys.UtmHasTtclid}:true", sessionId);
            if (adTraffic.ReferrerPresent) sink.Raise(SignalKeys.UtmReferrerPresent, sessionId);
            if (adTraffic.ReferrerMismatch) sink.Raise($"{SignalKeys.UtmReferrerMismatch}:true", sessionId);

            if (adTraffic.SourceHash != null)
                sink.Raise($"{SignalKeys.UtmSourceHash}:{adTraffic.SourceHash}", sessionId);
            if (adTraffic.MediumHash != null)
                sink.Raise($"{SignalKeys.UtmMediumHash}:{adTraffic.MediumHash}", sessionId);
            if (adTraffic.CampaignHash != null)
                sink.Raise($"{SignalKeys.UtmCampaignHash}:{adTraffic.CampaignHash}", sessionId);
            if (adTraffic.ClickIdHash != null)
                sink.Raise($"{SignalKeys.UtmClickIdHash}:{adTraffic.ClickIdHash}", sessionId);
        }

        // --- PII detection ---
        var result = QueryStringSanitizer.DetectPii(queryString);
        if (!result.HasPii)
            return Task.FromResult(None());

        var piiTypes = string.Join(",", result.DetectedTypes);
        sink.Raise(SignalKeys.PrivacyQueryPiiDetected, sessionId);
        sink.Raise($"{SignalKeys.PrivacyQueryPiiTypes}:{piiTypes}", sessionId);

        if (!context.Request.IsHttps)
        {
            sink.Raise(SignalKeys.PrivacyUnencryptedPii, sessionId);
            _logger.LogWarning("PII detected in unencrypted query string: types={PiiTypes}", piiTypes);
        }
        else
        {
            _logger.LogDebug("PII detected in query string: types={PiiTypes}", piiTypes);
        }

        return Task.FromResult(Single(DetectionContribution.Info(
            Name,
            Category,
            $"Query string contains PII parameters: {piiTypes}")));
    }
}