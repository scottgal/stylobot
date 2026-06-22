namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     One row of per-archetype, per-observed-UA-family drift distribution
///     written by <c>IdentityWeightCalibrationService.RunOnceAsync</c> after
///     centroid refinement. Captures whether the archetype's descendants
///     actually sit near its centroid (low drift = correctly sized) or are
///     drifting far (high drift = umbrella seeded wrong / catchment too wide).
///     <para>
///         The <see cref="MatchesAssertedUa"/> flag is load-bearing:
///         <list type="bullet">
///             <item><c>true</c> + high drift = archetype claims the right UA family but its centroid is in the wrong region. Refine centroid faster / shrink mask.</item>
///             <item><c>false</c> + non-trivial count = archetype is catching observations whose UA disagrees with what the archetype asserts. Over-claiming territory. Lower asserted-UA confidence / global shrink.</item>
///         </list>
///     </para>
///     See <c>docs/superpowers/specs/2026-06-21-adaptive-learning-loop-design.md</c> §2.B.
/// </summary>
public sealed record ArchetypeDriftMetric
{
    /// <summary>Archetype this row describes. Matches <c>identity_archetypes.archetype_id</c>.</summary>
    public required string ArchetypeId { get; init; }

    /// <summary>UA family observed on the descendant fingerprints' observations. Empty string when null on the observation row.</summary>
    public required string UaFamily { get; init; }

    /// <summary>
    ///     True when <see cref="UaFamily"/> equals the archetype's
    ///     <c>AssertedUaFamily</c> (case-insensitive). False when the
    ///     archetype asserts no UA (treated as universal -- never matches)
    ///     or when the UA disagrees.
    /// </summary>
    public required bool MatchesAssertedUa { get; init; }

    /// <summary>Number of descendant observations in this slice.</summary>
    public required int DescendantCount { get; init; }

    /// <summary>Mean L2 distance from observation vector to archetype centroid.</summary>
    public required double MeanL2ToCentroid { get; init; }

    /// <summary>Sample variance of L2 distances. Zero when DescendantCount &lt; 2.</summary>
    public required double VarianceL2ToCentroid { get; init; }

    /// <summary>90th-percentile L2 distance. Used for umbrella bloat detection (Phase 3).</summary>
    public required double P90L2ToCentroid { get; init; }

    /// <summary>When this row was computed.</summary>
    public required DateTime CalibratedAt { get; init; }
}
