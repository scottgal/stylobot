namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Domain record for one fingerprint shape — centroid, weights, and the metadata that
///     describes how the shape has evolved. Mirrors a row in the <c>fingerprints</c> table.
/// </summary>
public sealed record Fingerprint
{
    public required string FingerprintId { get; init; }
    public required float[] Centroid { get; init; }
    public required int CentroidMaturity { get; init; }
    public required float[] Weights { get; init; }
    public required int MemberCount { get; init; }
    public required int ObservationCount { get; init; }
    public required int CorrectionCount { get; init; }
    public required DateTime FirstSeen { get; init; }
    public required DateTime LastSeen { get; init; }
    public required double Quality { get; init; }
    public string? ArchetypeOrigin { get; init; }
    public required string InferredClientType { get; init; }
    public required double InferredTypeConfidence { get; init; }
    public required DateTime InferredTypeChangedAt { get; init; }
    public double CachedBotProbability { get; init; }
    public string? CachedRiskBand { get; init; }
    public DateTime? CachedScoreUpdatedAt { get; init; }

    /// <summary>
    ///     EWMA-smoothed fraction of recent matches that landed in the ambiguity band
    ///     (Pass 2 correction, rotation candidate, L1 confirm fail, allocation). High
    ///     values reveal boundary-probing — see task #42.
    /// </summary>
    public double AmbiguityPersistence { get; init; }

    /// <summary>
    ///     Matcher-projected name. Composed at allocation and on significant drift via
    ///     <c>FingerprintNameComposer.Compose</c> from the matched archetype + UA
    ///     characterization. Pure deterministic projection of shape -- no LLM, no operator
    ///     input. Nullable on the migration boundary; the matcher backfills on next match.
    /// </summary>
    public string? InducedName { get; init; }

    /// <summary>UTC timestamp when <see cref="InducedName"/> was last computed.</summary>
    public DateTime? InducedNameUpdatedAt { get; init; }

    /// <summary>
    ///     LLM-derived characterization name. Written by the always-on LFU-driven namer
    ///     when it walks the hot-N and finds this fingerprint missing or stale LLM coverage.
    ///     Independent of <see cref="InducedName"/> -- the matcher never overwrites this.
    /// </summary>
    public string? LlmName { get; init; }

    /// <summary>UTC timestamp when <see cref="LlmName"/> / <see cref="LlmDescription"/> were last written.</summary>
    public DateTime? LlmEvaluatedAt { get; init; }

    /// <summary>
    ///     LLM-derived prose description companion to <see cref="LlmName"/>. Surfaced on the
    ///     dashboard detail surface; not used for matching or scoring.
    /// </summary>
    public string? LlmDescription { get; init; }

    /// <summary>
    ///     Operator pin. Set explicitly via the dashboard editor when a human wants this
    ///     fingerprint to display a specific name regardless of induced / LLM output.
    ///     When non-null this wins the resolution race -- the resolver returns this first.
    /// </summary>
    public string? GivenName { get; init; }

    /// <summary>UTC timestamp when <see cref="GivenName"/> was last set or cleared.</summary>
    public DateTime? GivenNameUpdatedAt { get; init; }

    /// <summary>Operator identity (user id / email) that last wrote <see cref="GivenName"/>. Audit trail.</summary>
    public string? GivenNameOperatorId { get; init; }

    /// <summary>
    ///     The reference centroid that drift is measured against. Seeded at allocation
    ///     from the matched archetype's centroid -- archetypes ARE the cold-start root,
    ///     not a placeholder waiting for "real" data. Replaced by <c>BotClusterService</c>
    ///     snapshots when the fingerprint's cluster produces a data-driven community mean,
    ///     so a Chrome-142 release that shifts the population's centroids gets reflected
    ///     in every member fingerprint's root within one clustering cycle even though
    ///     the seed archetype YAML is now stale. Every change writes a row to
    ///     <c>fingerprint_root_history</c> so the dashboard can show the evolution chain.
    ///     Nullable only on the migration boundary -- legacy rows are backfilled on
    ///     first startup. Runtime steady state: never null. A null at request time is a
    ///     bug, not a "calibrating" state.
    /// </summary>
    public float[]? RootCentroid { get; init; }

    /// <summary>UTC timestamp when <see cref="RootCentroid"/> was last set.</summary>
    public DateTime? RootCentroidAt { get; init; }

    /// <summary>
    ///     Lineage marker for <see cref="RootCentroid"/>: <c>archetype:&lt;id&gt;</c> at
    ///     allocation, <c>cluster:&lt;id&gt;</c> after a BotClusterService snapshot,
    ///     <c>verifiedbot:&lt;name&gt;</c> for the verifiedbot allocation path,
    ///     <c>bootstrap</c> for legacy rows backfilled on the migration boundary.
    /// </summary>
    public string? RootSource { get; init; }

    /// <summary>
    ///     Persistent verification state for the CLAIMED identity attached to
    ///     this fingerprint. Per gap analysis 2026-06-15 (Gap #4) -- previously
    ///     trust was an in-memory one-way latch on <c>SignatureCoordinator</c>
    ///     and vanished on process restart. Values:
    ///     <list type="bullet">
    ///         <item><c>unverified</c> -- default; no verifier has corroborated the claim yet.</item>
    ///         <item><c>verified</c> -- a verifier corroborated the claim (rDNS / NodeInfo / IP range / forward DNS).</item>
    ///         <item><c>spoofed</c> -- a verifier ran and refuted the claim (e.g. UA claims Googlebot but rDNS fails).</item>
    ///         <item><c>behaviourally-trusted</c> -- Gap #5: behavioural-trust accumulation across N consistent observations.</item>
    ///     </list>
    ///     The request-path verifier contributors read this + <see cref="VerifiedAt"/>
    ///     to short-circuit re-verification when still within <c>TrustOptions.TrustCacheTtl</c>.
    /// </summary>
    public string ClaimStatus { get; init; } = "unverified";

    /// <summary>
    ///     How the claim was verified. Values: <c>ip_range</c> (vendor-published CIDR
    ///     match), <c>fcrdns</c> (reverse + forward DNS round-trip), <c>forward_dns</c>
    ///     (claimed instance's A/AAAA records contain the client IP), <c>nodeinfo</c>
    ///     (fediverse NodeInfo lookup confirmed ActivityPub software), or
    ///     <c>behavioural-trust</c> (Gap #5: N consistent observations).
    ///     <c>null</c> when <see cref="ClaimStatus"/> is <c>unverified</c>.
    /// </summary>
    public string? VerificationMethod { get; init; }

    /// <summary>
    ///     UTC timestamp of first successful verification. Used by the request-path
    ///     short-circuit: if (now - VerifiedAt) is within <c>TrustOptions.TrustCacheTtl</c>
    ///     the verifier contributors emit <c>verifiedbot.cached</c> and skip
    ///     re-verification. <c>null</c> when <see cref="ClaimStatus"/> is <c>unverified</c>.
    /// </summary>
    public DateTime? VerifiedAt { get; init; }

    /// <summary>
    ///     Counter for behavioural-trust accumulation (Gap #5, separate work item):
    ///     incremented when a request matches the claimed identity's expected
    ///     behavioural pattern. When it crosses a configured threshold the
    ///     <see cref="ClaimStatus"/> transitions to <c>behaviourally-trusted</c>.
    /// </summary>
    public int TrustObservations { get; init; }
}

/// <summary>
///     Projection-time read-through of the score/verdict scalars a dashboard row needs,
///     resolved from the canonical <see cref="Fingerprint"/> in the LFU. The fingerprint
///     LFU is the single source for these values; the dashboard's
///     <c>SignatureAggregateCache</c> reads them through this record at projection time —
///     EXACTLY like the display name is read through
///     <see cref="IFingerprintStore.GetResolvedNamesBySignaturesAsync"/> — instead of
///     storing a stale parallel copy per aggregate row.
///     <para>
///     Population from the fingerprint: <see cref="BotProbability"/> =
///     <see cref="Fingerprint.CachedBotProbability"/>; <see cref="RiskBand"/> =
///     <see cref="Fingerprint.CachedRiskBand"/>; <see cref="BotType"/> =
///     <see cref="Fingerprint.InferredClientType"/> (the fingerprint's REAL inferred type —
///     null when the fingerprint carries no type; NEVER the literal placeholder "Unknown",
///     so the view falls through to entity-id / UA-family labels);
///     <see cref="Confidence"/> = <see cref="Fingerprint.InferredTypeConfidence"/>;
///     <see cref="IsBot"/> derived as <c>CachedBotProbability &gt;= 0.5</c> (the is-bot
///     derivation the dashboard projection paths already use — the store carries no
///     reachable BotThreshold); <see cref="IsVerifiedBot"/> from the fingerprint's
///     <c>ClaimStatus == "verified"</c>. The fingerprint carries no threat scalars, so
///     <see cref="ThreatScore"/> / <see cref="ThreatBand"/> default to null.
///     </para>
/// </summary>
public sealed record ResolvedVerdict(
    double BotProbability,
    string? RiskBand,
    string? BotType,
    double Confidence,
    double? ThreatScore,
    string? ThreatBand,
    bool IsBot,
    bool IsVerifiedBot);

/// <summary>
///     A candidate result from the index search — fingerprint id, the distance components that
///     drove its inclusion, and (lazily) the loaded fingerprint row.
/// </summary>
public sealed record FingerprintCandidate(
    string FingerprintId,
    double CentroidScore,
    double BestObsScore);

/// <summary>
///     A detailed observation that has met an absorption threshold; carries everything the
///     absorption transaction needs without re-reading.
/// </summary>
public sealed record AbsorbableObservation
{
    public required long ObservationId { get; init; }
    public required string FingerprintId { get; init; }
    public required float[] Vector { get; init; }
    public required float[] Centroid { get; init; }
    public required int CentroidMaturity { get; init; }
    public required float[] Weights { get; init; }
    public required string InferredClientType { get; init; }

    /// <summary>
    ///     The UA family string emitted at the time this observation was recorded,
    ///     preserved on the row so the absorption path can pass it to the archetype
    ///     matcher's UA-family gate. Null on legacy rows recorded before the column
    ///     existed; the matcher treats null as "no gate" and falls back to unfiltered
    ///     scoring -- same behavior as before this field existed.
    /// </summary>
    public string? UaFamily { get; init; }

    /// <summary>
    ///     eTLD+1 owner of the request that produced this observation. Null on
    ///     pre-multi-domain rows recorded before the column existed; downstream
    ///     aggregators treat null as "unknown scope" and fall back to global counts.
    /// </summary>
    public string? Domain { get; init; }

    /// <summary>
    ///     Full lowercased port-stripped Host header of the request that produced this
    ///     observation. Null on pre-multi-domain rows.
    /// </summary>
    public string? Host { get; init; }
}
