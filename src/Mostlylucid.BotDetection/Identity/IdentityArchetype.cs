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

    /// <summary>
    ///     Per-dimension variance vector used for Mahalanobis-style scoring. Tight archetypes
    ///     have small variance (penalize even small deviations); broad umbrella archetypes have
    ///     large variance (tolerate larger deviations).
    ///
    ///     Null at construction time; populated either from YAML override
    ///     (<see cref="IdentityArchetypeYaml.VariancePerDimension"/>) or via the default-from-confidence
    ///     rule applied in <c>IdentityArchetypeRegistry.Compile</c>: variance[i] = max(epsilon, (1 - confidence[i])^2 * baseScale).
    /// </summary>
    public float[]? VarianceVector { get; init; }

    public int DescendantCount { get; init; }
    public DateTime LastRefinedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    ///     <c>client</c> = a traffic-source identity (Chrome Desktop, Mobile Chrome,
    ///     Googlebot, curl, Mastodon, etc.) -- the kind of label the dashboard should
    ///     display as "what is this visitor". <c>mode</c> = a request-shape archetype
    ///     (Chrome XHR, future Chrome SignalR, etc.) which exists ONLY to give the
    ///     nearest-archetype matcher a per-mode anchor so a real Chrome doing XHR
    ///     does not drift to googlebot / mastodon shapes. Mode archetypes ARE still
    ///     scored by <see cref="IdentityArchetypeRegistry.FindNearest"/> (they're
    ///     necessary priors), but they MUST NOT be used as the client identity for
    ///     naming or for the drift "Origin -> Current" comparison -- "Chrome
    ///     Desktop -> Chrome XHR" is a mode shift, not an identity drift, and
    ///     letting it surface as a client-identity change is the category error
    ///     the composite-browser-mode-fingerprints spec was designed to fix.
    ///
    ///     Sourced from the YAML's <c>archetype_role</c> field; defaults to
    ///     <c>client</c> when unset so the field is opt-in for the few mode-shaped
    ///     archetypes (chrome-xhr today; future signalr / websocket-upgrade /
    ///     prefetch entries when they pick up centroid data).
    /// </summary>
    public string ArchetypeRole { get; init; } = "client";

    /// <summary>True when <see cref="ArchetypeRole"/> is "mode" (case-insensitive).</summary>
    public bool IsMode => string.Equals(ArchetypeRole, "mode", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     Result of an archetype scan — the closest archetype to a given vector by plain cosine,
///     plus the score that drove the choice.
/// </summary>
public sealed record ArchetypeMatch(IdentityArchetype Archetype, double Score);
