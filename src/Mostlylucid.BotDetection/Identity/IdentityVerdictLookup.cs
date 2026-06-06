using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     A cached verdict pulled from the metastable fingerprint store via a primary
///     signature. Returned by <see cref="IdentityVerdictLookup.TryGetAsync"/>; consumed
///     by <c>SignatureVerdictGate</c> when composing with the per-signature aggregate.
/// </summary>
public sealed record IdentityCachedVerdict(
    string FingerprintId,
    double BotProbability,
    string? RiskBand,
    DateTime UpdatedAtUtc,
    int ObservationCount,
    string InferredClientType);

/// <summary>
///     Verdict-gate accessor over the metastable identity layer. Returns null when
///     Identity is disabled, no fingerprint binding exists, or the cached score has
///     never been written. Fails closed: any lookup exception falls back to null so a
///     bad identity row can't crash the gate path.
/// </summary>
public sealed class IdentityVerdictLookup
{
    private readonly ILogger<IdentityVerdictLookup> _logger;
    private readonly IFingerprintStore _store;
    private readonly bool _enabled;

    public IdentityVerdictLookup(
        ILogger<IdentityVerdictLookup> logger,
        IFingerprintStore store,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _store = store;
        _enabled = options.Value.Identity.Enabled;
    }

    public async Task<IdentityCachedVerdict?> TryGetAsync(string primarySignature, CancellationToken ct = default)
    {
        if (!_enabled) return null;
        if (string.IsNullOrEmpty(primarySignature)) return null;

        try
        {
            // Two dict hits served by the existing LFU caches inside SqliteFingerprintStore:
            //   primarySig -> fingerprintId  (via _fingerprintIdByPrimarySig)
            //   fingerprintId -> Fingerprint (via _fingerprintById, includes cached_* columns)
            // The fingerprint row IS the verdict cache. No parallel verdict-by-signature dict.
            // No "cached_score_updated_at IS NOT NULL" SQL gate.
            var fingerprintId = await _store.LookupFingerprintIdAsync(primarySignature, ct);
            if (string.IsNullOrEmpty(fingerprintId)) return null;

            var fp = await _store.GetFingerprintAsync(fingerprintId, ct);
            if (fp is null) return null;
            if (fp.CachedScoreUpdatedAt is null) return null; // not yet matured

            return new IdentityCachedVerdict(
                FingerprintId:       fp.FingerprintId,
                BotProbability:      fp.CachedBotProbability,
                RiskBand:            fp.CachedRiskBand,
                UpdatedAtUtc:        fp.CachedScoreUpdatedAt.Value,
                ObservationCount:    fp.ObservationCount,
                InferredClientType:  fp.InferredClientType ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity verdict lookup failed for primary signature");
            return null;
        }
    }
}
