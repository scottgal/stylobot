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

    /// <summary>
    ///     The pre-L2-normalisation centroid built directly from the YAML's raw dimension values.
    ///     <see cref="Centroid"/> is unit-length for cosine matching; <c>CentroidRaw</c> preserves
    ///     the original magnitudes for variance-aware scoring.
    ///
    ///     Null on archetypes built before this property was added (backwards-compat); callers
    ///     fall back to <see cref="Centroid"/> when null. Always populated by
    ///     <c>IdentityArchetypeRegistry.Compile</c> from this commit forward.
    /// </summary>
    public float[]? CentroidRaw { get; init; }

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
    ///     scored by <see cref="IdentityArchetypeRegistry.FindNearest(float[], string?)"/> (they're
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

    /// <summary>
    ///     The raw <c>hdr.ua_family</c> string the YAML asserts (or the well-known-bot
    ///     display name for arcjet-promoted ghosts). Preserved separately from the
    ///     <see cref="Centroid"/>'s 2-dim LSH encoding of the same value because the LSH
    ///     hash has ~4 buckets to disambiguate hundreds of UA families -- relying on cosine
    ///     in the hash space let a Chrome observation match a "freshping" archetype at
    ///     0.95 just because their hashes collided.
    ///
    ///     Consumed by <see cref="IdentityArchetypeRegistry.FindNearest(float[], string?)"/> as a hard
    ///     candidacy gate: when the caller supplies the observation's UA family string,
    ///     archetypes whose <c>AssertedUaFamily</c> differs are dropped before cosine
    ///     scoring. Archetypes that don't assert a UA family (this field is null) are
    ///     universal candidates.
    /// </summary>
    public string? AssertedUaFamily { get; init; }

    /// <summary>
    ///     Phase 3 umbrella-shrinkage knob (2026-06-21). Scalar &#x2208; [0.05, 1.0]
    ///     applied to <see cref="VarianceVector"/> (and the default-variance
    ///     fallback) inside the matcher's masked-cosine score. Below 1.0 tightens
    ///     the archetype's effective catchment -- its Gaussian per-dim
    ///     log-likelihood penalises deviation more, so descendants drifting far
    ///     from the centroid score lower and adjacent archetypes win their
    ///     rightful territory. Calibration multiplies by <c>(1 - shrinkRate)</c>
    ///     per cycle when umbrella leakage is detected (see spec §2.C action
    ///     ladder); the floor at 0.05 prevents a confused archetype from
    ///     vanishing entirely. Default 1.0 = no narrowing, identical to the
    ///     pre-Phase-3 matcher behaviour.
    /// </summary>
    public double VarianceMultiplier { get; init; } = 1.0;

    // ===== Phase 4 centroid-mobility diagnostics (2026-06-21, spec §2.D) ======
    // Operational telemetry, not load-bearing for the matcher. Surfaced on the
    // /admin/learning/health endpoint so the maintainer can answer "is this
    // archetype pinned?" without spelunking the SQLite WAL. All fields are
    // in-memory only -- they reset on process restart, which is fine because
    // calibration repopulates them on the next cycle.

    /// <summary>
    ///     L2 distance between this archetype's centroid before and after the
    ///     most recent refinement. Zero on the cycle when no descendants
    ///     existed. Used together with <see cref="PinCycles"/> to surface
    ///     "centroid not moving despite high variance" -- the spec §2.D
    ///     pinning signal.
    /// </summary>
    public double CentroidDeltaLastCycle { get; init; }

    /// <summary>
    ///     Consecutive calibration cycles where
    ///     <see cref="CentroidDeltaLastCycle"/> stayed below the pin threshold
    ///     (see <c>CentroidMobilityOptions.PinDeltaThreshold</c>). Resets to
    ///     zero whenever the centroid moves meaningfully. High values + high
    ///     <see cref="DescendantVarianceLastCycle"/> = "stuck in the wrong
    ///     position with disagreeing descendants" = the case the maintainer
    ///     called out: pinned, with neighbours possibly creeping in.
    /// </summary>
    public int PinCycles { get; init; }

    /// <summary>
    ///     Mean per-dim sample variance of descendant centroids computed at the
    ///     most recent refinement. Feeds the adaptive-&#x3B1; formula (low variance
    ///     &#x2192; high &#x3B1;, high variance &#x2192; low &#x3B1;) -- the spec §2.D rule that
    ///     keeps the centroid stable when descendants agree and lets umbrella
    ///     shrinkage do the cleanup when they disagree.
    /// </summary>
    public double DescendantVarianceLastCycle { get; init; }

    /// <summary>
    ///     Id of the closest other archetype at the end of the most recent
    ///     calibration cycle. Null when no other archetype exists. Together
    ///     with <see cref="NearestNeighbourDistance"/> this surfaces the
    ///     "moves closer to others" concern (spec §2.D rule 3 convergence
    ///     detection) without requiring repulsion -- v1 is observe-only.
    /// </summary>
    public string? NearestNeighbourId { get; init; }

    /// <summary>L2 distance to the centroid identified by <see cref="NearestNeighbourId"/>.</summary>
    public double NearestNeighbourDistance { get; init; }

    /// <summary>
    ///     Phase 5 auto-regrowth counter (2026-06-21). Consecutive calibration
    ///     cycles where neither leakage nor bloat fired -- the archetype is
    ///     behaving healthily. When this crosses
    ///     <c>UmbrellaShrinkageOptions.RegrowAfterCycles</c>, the calibration
    ///     starts walking <see cref="VarianceMultiplier"/> back toward 1.0 by
    ///     <c>RegrowRate</c> per cycle. Resets to zero on any shrinkage event.
    ///     Lets an archetype recover its catchment after a transient population
    ///     shift (e.g. a Chrome version upgrade) without operator intervention.
    /// </summary>
    public int HealthyCycles { get; init; }
}

/// <summary>
///     Result of an archetype scan — the closest archetype to a given vector by plain cosine,
///     plus the score that drove the choice.
/// </summary>
public sealed record ArchetypeMatch(IdentityArchetype Archetype, double Score);

/// <summary>
///     Storage-shaped projection of one <c>identity_archetypes</c> row. Carries
///     only the columns relevant to the dual-purpose catalogue surface
///     (<see cref="IFingerprintStore.GetByCatalogueKindAsync"/> +
///     <see cref="IFingerprintStore.UpsertCentroidAsync"/>) so the
///     mode-centroid catalogue (kind: <c>browser_mode</c>) can stay decoupled
///     from <see cref="IdentityArchetype"/>'s richer YAML-loader-facing shape.
///     <see cref="Maturity"/> is the centroid maturity counter (descendant
///     count for the identity catalogue; per-mode observation absorption
///     count for the browser-mode catalogue).
/// </summary>
public sealed record IdentityArchetypeRow(
    string ArchetypeId,
    string CatalogueKind,
    float[] Centroid,
    double Maturity);
