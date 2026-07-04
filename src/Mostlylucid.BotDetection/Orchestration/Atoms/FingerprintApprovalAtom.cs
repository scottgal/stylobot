using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that checks if the current fingerprint has
///     been manually approved. If so, validates locked-dimension constraints
///     against live sink signals.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>FingerprintApprovalContributor</c>. Turns API-key access from
///         "key = access" into a behavioural contract: the fingerprint must
///         match approved dimensions (country, UA, IP range, etc.) or the
///         approval is void. A stolen key from a different environment is
///         useless.
///     </para>
///     <para>
///         Approval is a strong human signal (-0.4 delta) when all locked
///         dimensions match. Dimension mismatch is a strong bot signal (+0.3
///         delta) -- something changed. Config via
///         <see cref="IDetectorConfigProvider"/> under manifest name
///         "FingerprintApproval". Priority 24.
///     </para>
/// </remarks>
public sealed class FingerprintApprovalAtom : DetectorAtomBase
{
    private readonly IFingerprintApprovalStore _approvalStore;
    private readonly ILogger<FingerprintApprovalAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public FingerprintApprovalAtom(
        IFingerprintApprovalStore approvalStore,
        ILogger<FingerprintApprovalAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "FingerprintApproval", category: "FingerprintApproval")
    {
        _approvalStore = approvalStore;
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 24;

    private double DimensionMismatchConfidence =>
        _configProvider.GetParameter(Name, "dimension_mismatch_confidence", 0.3);

    private double DimensionMismatchWeight =>
        _configProvider.GetParameter(Name, "dimension_mismatch_weight", 2.0);

    private double ApprovalHumanConfidence =>
        _configProvider.GetParameter(Name, "unlocked_approval_confidence", -0.4);

    private double ApprovalHumanWeight =>
        _configProvider.GetParameter(Name, "approval_human_weight", 2.0);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        try
        {
            var signature = sink.ReadHint(SignalKeys.PrimarySignature);
            if (string.IsNullOrEmpty(signature))
                return None();

            var approval = await _approvalStore.GetAsync(signature, ct);
            if (approval is null)
                return None();

            // Approval expired or revoked -- signal but no contribution
            if (!approval.IsActive)
            {
                sink.Raise($"{SignalKeys.ApprovalStatus}:{(approval.RevokedAt.HasValue ? "revoked" : "expired")}", sessionId);
                return None();
            }

            // Check locked dimensions against live sink hints
            if (approval.LockedDimensions.Count > 0)
            {
                var (allMatch, mismatches) = LockedDimensionChecker.Check(
                    approval.LockedDimensions,
                    key => LookupDimensionValue(sink, key));

                sink.Raise(SignalKeys.ApprovalVerified, sessionId);
                if (allMatch)
                    sink.Raise(SignalKeys.ApprovalLockedDimensionsOk, sessionId);

                if (!allMatch)
                {
                    var mismatchList = string.Join(", ", mismatches);
                    sink.Raise($"{SignalKeys.ApprovalDimensionMismatch}:{mismatchList}", sessionId);

                    _logger.LogWarning(
                        "Fingerprint approval dimension mismatch for {Signature}: {Mismatches}",
                        signature, mismatchList);

                    return Single(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = DimensionMismatchConfidence,
                        Weight = DimensionMismatchWeight,
                        Reason = $"Approved fingerprint violated locked dimensions: {mismatchList}"
                    });
                }
            }
            else
            {
                sink.Raise(SignalKeys.ApprovalVerified, sessionId);
                sink.Raise(SignalKeys.ApprovalLockedDimensionsOk, sessionId);
            }

            // All checks pass -- emit strong human signal
            sink.Raise($"{SignalKeys.ApprovalJustification}:{approval.Justification}", sessionId);
            if (approval.ExpiresAt.HasValue)
                sink.Raise($"{SignalKeys.ApprovalExpiresAt}:{approval.ExpiresAt.Value:O}", sessionId);
            sink.Raise($"{SignalKeys.ApprovalStatus}:active", sessionId);

            _logger.LogDebug(
                "Fingerprint approval verified for {Signature}: {Justification}, {DimCount} locked dimensions OK",
                signature, approval.Justification, approval.LockedDimensions.Count);

            return Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = ApprovalHumanConfidence,
                Weight = ApprovalHumanWeight,
                Reason = $"Fingerprint manually approved: {approval.Justification}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FingerprintApproval check failed");
            return None();
        }
    }

    /// <summary>
    ///     Locked-dimension lookup: sink hint first (Model 2 value after colon),
    ///     falls back to HttpContext.Items when a boundary sensor hasn't
    ///     re-raised the value onto the sink yet. Returns the string value or
    ///     null if not present; LockedDimensionChecker handles the type coerce
    ///     on comparison.
    /// </summary>
    private object? LookupDimensionValue(SignalSink sink, string key)
    {
        var hint = sink.ReadHint(key);
        if (hint is not null) return hint;

        var context = _httpContextAccessor.HttpContext;
        return context is not null && context.Items.TryGetValue(key, out var raw) ? raw : null;
    }
}
