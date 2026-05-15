using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     A cached verdict pulled from the metastable fingerprint store via a primary signature.
///     Returned by <see cref="IdentityVerdictLookup.TryGetAsync"/>; consumed by
///     <c>SignatureVerdictGate</c> when composing with the per-signature aggregate.
/// </summary>
public sealed record IdentityCachedVerdict(
    string FingerprintId,
    double BotProbability,
    string? RiskBand,
    DateTime UpdatedAtUtc,
    int ObservationCount,
    string InferredClientType);

/// <summary>
///     Single-call lookup: <c>fingerprint_keys[primary_signature] → fingerprints</c> →
///     cached verdict. Used by the verdict gate so a rotated identity (new IP+UA, same
///     metastable shape) inherits its prior verdict instead of paying for a fresh
///     pipeline pass. The lookup is L1-only (point lookup); the L2 vector cosine path
///     stays in <c>FingerprintMatchContributor</c> where bots pay for it.
///
///     Returns null when Identity is disabled, the primary signature has no
///     fingerprint binding yet, or the matched fingerprint has never had its cached
///     score updated. The gate falls back to the per-signature verdict in those cases.
/// </summary>
public sealed class IdentityVerdictLookup
{
    private readonly ILogger<IdentityVerdictLookup> _logger;
    private readonly SqliteFingerprintStore _store;
    private readonly bool _enabled;

    public IdentityVerdictLookup(
        ILogger<IdentityVerdictLookup> logger,
        SqliteFingerprintStore store,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _store = store;
        _enabled = options.Value.Identity.Enabled;
    }

    public async Task<IdentityCachedVerdict?> TryGetAsync(string primarySignature, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrEmpty(primarySignature)) return null;

        try
        {
            var fpId = await _store.LookupFingerprintIdAsync(primarySignature, ct);
            if (fpId is null) return null;

            var fp = await _store.GetFingerprintAsync(fpId, ct);
            if (fp is null || fp.CachedScoreUpdatedAt is null) return null;

            return new IdentityCachedVerdict(
                FingerprintId: fpId,
                BotProbability: fp.CachedBotProbability,
                RiskBand: fp.CachedRiskBand,
                UpdatedAtUtc: fp.CachedScoreUpdatedAt.Value,
                ObservationCount: fp.ObservationCount,
                InferredClientType: fp.InferredClientType);
        }
        catch (Exception ex)
        {
            // Lookup failure must NOT cascade into the gate path — Miss is a safe
            // fallback that just runs the pipeline as if Identity didn't exist.
            _logger.LogWarning(ex, "Identity verdict lookup failed for primary signature");
            return null;
        }
    }
}
