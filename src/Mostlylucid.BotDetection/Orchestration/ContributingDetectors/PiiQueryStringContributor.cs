using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Privacy;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Privacy contributor that detects PII in query strings and emits informational signals.
///     Also extracts UTM / click-ID parameters and emits hashed ad-traffic signals BEFORE
///     the sanitizer strips them - enabling downstream click-fraud detection with zero PII stored.
///     Runs in Wave 0 at Priority 8 (very early, before most detectors).
/// </summary>
public class PiiQueryStringContributor : ContributingDetectorBase
{
    private readonly ILogger<PiiQueryStringContributor> _logger;
    private readonly PiiHasher _hasher;

    public PiiQueryStringContributor(
        ILogger<PiiQueryStringContributor> logger,
        PiiHasher hasher)
    {
        _logger = logger;
        _hasher = hasher;
    }

    public override string Name => "PiiQueryString";
    public override int Priority => 8;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var queryString = state.HttpContext.Request.QueryString.Value;

        if (string.IsNullOrEmpty(queryString))
            return Task.FromResult(None());

        var request = state.HttpContext.Request;
        var referer = request.Headers.Referer.ToString();

        // --- UTM / click-ID detection (before PII stripping) ---
        var adTraffic = QueryStringSanitizer.DetectAdTrafficParams(
            queryString,
            string.IsNullOrEmpty(referer) ? null : referer,
            _hasher.GetKey());

        if (adTraffic.UtmPresent)
        {
            state.WriteSignal(SignalKeys.UtmPresent, true);
            state.WriteSignal(SignalKeys.UtmSourcePlatform, adTraffic.SourcePlatform);
            state.WriteSignal(SignalKeys.UtmHasGclid, adTraffic.HasGclid);
            state.WriteSignal(SignalKeys.UtmHasFbclid, adTraffic.HasFbclid);
            state.WriteSignal(SignalKeys.UtmHasMsclkid, adTraffic.HasMsclkid);
            state.WriteSignal(SignalKeys.UtmHasTtclid, adTraffic.HasTtclid);
            state.WriteSignal(SignalKeys.UtmReferrerPresent, adTraffic.ReferrerPresent);
            state.WriteSignal(SignalKeys.UtmReferrerMismatch, adTraffic.ReferrerMismatch);

            if (adTraffic.SourceHash != null)
                state.WriteSignal(SignalKeys.UtmSourceHash, adTraffic.SourceHash);
            if (adTraffic.MediumHash != null)
                state.WriteSignal(SignalKeys.UtmMediumHash, adTraffic.MediumHash);
            if (adTraffic.CampaignHash != null)
                state.WriteSignal(SignalKeys.UtmCampaignHash, adTraffic.CampaignHash);
            if (adTraffic.ClickIdHash != null)
                state.WriteSignal(SignalKeys.UtmClickIdHash, adTraffic.ClickIdHash);
        }

        // --- PII detection (unchanged) ---
        var result = QueryStringSanitizer.DetectPii(queryString);

        if (!result.HasPii)
            return Task.FromResult(None());

        var piiTypes = string.Join(",", result.DetectedTypes);
        state.WriteSignal(SignalKeys.PrivacyQueryPiiDetected, true);
        state.WriteSignal(SignalKeys.PrivacyQueryPiiTypes, piiTypes);

        var isHttps = request.IsHttps;
        if (!isHttps)
        {
            state.WriteSignal(SignalKeys.PrivacyUnencryptedPii, true);
            _logger.LogWarning("PII detected in unencrypted query string: types={PiiTypes}", piiTypes);
        }
        else
        {
            _logger.LogDebug("PII detected in query string: types={PiiTypes}", piiTypes);
        }

        var contribution = DetectionContribution.Info(
            Name,
            "Privacy",
            $"Query string contains PII parameters: {piiTypes}");

        return Task.FromResult(Single(contribution));
    }
}
