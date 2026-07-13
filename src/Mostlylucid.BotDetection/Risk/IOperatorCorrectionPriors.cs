using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Risk;

/// <summary>
///     A ground-truth label an operator applied to a fingerprint via the commercial
///     "correct decision" control (Not Bot / Bot + bot-type). Consumed by
///     <see cref="SignatureRiskVerdictComposer"/> as a high-confidence PRIOR that biases the
///     verdict toward the operator's ground truth. It is deliberately NOT a decision-path
///     override: the composer's behaviour pins (hostile / confirmed-bad) still run after the
///     bias, so a "human"-labelled fingerprint that later attacks is still caught. No bypass.
/// </summary>
public sealed record OperatorCorrectionPrior(string Label, string? BotType, DateTime At)
{
    /// <summary>The operator asserted this fingerprint is a bot.</summary>
    public bool IsBot => string.Equals(Label, "bot", StringComparison.OrdinalIgnoreCase);

    /// <summary>The operator asserted this fingerprint is human (not a bot).</summary>
    public bool IsHuman => string.Equals(Label, "human", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     FOSS-owned, in-memory registry of operator corrections, keyed by fingerprint id. The
///     commercial correction store calls <see cref="Set"/> on write (and on cross-gateway
///     propagation); the verdict-input build path reads via <see cref="TryGet"/> at compose
///     time, so there is no hot-path store round-trip. This is a nullable seam: FOSS registers
///     the in-memory default but nothing populates it unless the commercial control is present,
///     so no correction ever biases a verdict in a pure-FOSS deployment.
/// </summary>
public interface IOperatorCorrectionPriors
{
    /// <summary>Record (or replace) the operator's ground-truth label for a fingerprint.</summary>
    void Set(string fingerprintId, OperatorCorrectionPrior prior);

    /// <summary>Read the current correction for a fingerprint, if any.</summary>
    bool TryGet(string fingerprintId, out OperatorCorrectionPrior prior);

    /// <summary>Forget the correction for a fingerprint (operator cleared it).</summary>
    void Clear(string fingerprintId);
}

/// <summary>
///     Bounded in-memory default. Operator corrections are low-cardinality (operator-curated),
///     so a small LFU cache holds the working set with no persistence of its own -- the
///     commercial store owns durability and re-populates on startup / propagation.
/// </summary>
public sealed class InMemoryOperatorCorrectionPriors : IOperatorCorrectionPriors
{
    private readonly BoundedCache<string, OperatorCorrectionPrior> _map = new(maxSize: 5_000);

    public void Set(string fingerprintId, OperatorCorrectionPrior prior)
    {
        if (!string.IsNullOrEmpty(fingerprintId) && prior is not null)
            _map.Set(fingerprintId, prior);
    }

    public bool TryGet(string fingerprintId, out OperatorCorrectionPrior prior)
    {
        if (!string.IsNullOrEmpty(fingerprintId) && _map.TryGet(fingerprintId, out var p))
        {
            prior = p;
            return true;
        }

        prior = null!;
        return false;
    }

    public void Clear(string fingerprintId)
    {
        if (!string.IsNullOrEmpty(fingerprintId))
            _map.Remove(fingerprintId);
    }
}
