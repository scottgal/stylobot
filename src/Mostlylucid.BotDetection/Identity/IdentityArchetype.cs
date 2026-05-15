namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     A pre-loaded canonical traffic shape used as a template for new-fingerprint allocation,
///     a calibration label source, and the source of <see cref="Fingerprint.InferredClientType"/>.
///     See docs/architecture/fingerprint-match.md.
/// </summary>
public sealed record IdentityArchetype
{
    public required string ArchetypeId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string ArchetypeKind { get; init; }
    public required float[] Centroid { get; init; }
    public required float[] DimensionMask { get; init; }
    public int DescendantCount { get; init; }
    public DateTime LastRefinedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
///     Result of an archetype scan — the closest archetype to a given vector by plain cosine,
///     plus the score that drove the choice.
/// </summary>
public sealed record ArchetypeMatch(IdentityArchetype Archetype, double Score);
