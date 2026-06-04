namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     One row of the <c>fingerprint_modes</c> table. A fingerprint can carry
///     several mode rows — one per request shape the same identity plays during
///     a session (navigation, xhr, sub-resource, signalr-negotiate, etc.).
///     Each row is an independent centroid + weights + maturity surface; the
///     parent <see cref="Fingerprint.Centroid"/> stays as the rollup (weighted
///     mean of children, recomputed on a schedule-coordinator tick).
///
///     See docs/architecture/composite-browser-mode-fingerprints.md.
/// </summary>
public sealed record FingerprintBrowserMode
{
    /// <summary>Parent fingerprint id.</summary>
    public required string FingerprintId { get; init; }

    /// <summary>Mode id from <c>Definitions/BrowserModes/*.yaml</c>.</summary>
    public required string ModeId { get; init; }

    /// <summary>EWMA centroid for this mode only.</summary>
    public required float[] Centroid { get; init; }

    /// <summary>Number of absorbed observations folded into <see cref="Centroid"/>.</summary>
    public required int CentroidMaturity { get; init; }

    /// <summary>Per-mode weight vector (stability learning is scoped to the mode).</summary>
    public required float[] Weights { get; init; }

    /// <summary>Total observation count (including unabsorbed). Used by the mix-deviation axis.</summary>
    public required int ObservationCount { get; init; }

    public required DateTime FirstSeen { get; init; }
    public required DateTime LastSeen { get; init; }

    /// <summary>Nearest archetype id for this mode's centroid, recomputed on a tick.</summary>
    public string? InferredArchetype { get; init; }

    /// <summary>Score of the <see cref="InferredArchetype"/> match (presence-aware similarity).</summary>
    public double? InferredConfidence { get; init; }
}
