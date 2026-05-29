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
        try
        {
            return await _store.GetCachedVerdictForSignatureAsync(primarySignature, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity verdict lookup failed for primary signature");
            return null;
        }
    }
}
