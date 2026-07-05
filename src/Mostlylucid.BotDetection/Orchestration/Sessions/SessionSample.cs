namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Compact summary of a single response, emitted by the summary
///     escalator into the shared per-domain session store. Payload type on
///     the escalator → session-store hop.
/// </summary>
/// <remarks>
///     <para>
///         <b>Scale-conscious.</b> Session state is a shared per-domain
///         store (see <see cref="SessionStore"/>), never per-fingerprint
///         sinks -- one sink per fingerprint does not scale on real
///         traffic. Signals raised into the shared store are tagged by
///         <see cref="FingerprintId"/>; the session atom filters/aggregates
///         by that when detecting fingerprint shift.
///     </para>
///     <para>
///         <b>Distinct from</b>
///         <c>IntentPromptBuilder.SessionSummary</c> (a prompt string) and
///         <c>DashboardSessionSummary</c> (a dashboard aggregate). This
///         record is the per-response payload the response escalator emits.
///     </para>
///     <para>
///         Kept compact by design: the LLM escalator's job is to carry the
///         full HttpContext + blackboard when that path fires. This summary
///         is what the session atom uses to detect whether the aggregate is
///         drifting the fingerprint's identity -- everything not
///         load-bearing for shift detection lives on the LLM path.
///     </para>
/// </remarks>
public sealed record SessionSample
{
    /// <summary>
    ///     Fingerprint ID resolved during this request cycle (L1 → L2 → L3
    ///     match already completed). Session code sees a single resolved
    ///     ID, not "which level"; the request pipeline handles the level
    ///     resolution before the summary escalator fires.
    /// </summary>
    public required string FingerprintId { get; init; }

    /// <summary>
    ///     <c>SiteProfile.Id</c> for the domain that served this response.
    ///     Provides tenant isolation on the shared session store -- one
    ///     internal partition per site so a busy site's shaped eviction
    ///     never trims a quieter site's sessions.
    /// </summary>
    public required string SiteId { get; init; }

    /// <summary>Response timestamp. Feeds <see cref="SessionAggregate.LastSample"/>.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    ///     Per-response verdict: how bot-like was this specific request.
    ///     Aggregated by the session atom into
    ///     <see cref="SessionAggregate.MeanBotProbability"/> +
    ///     <see cref="SessionAggregate.MaxBotProbability"/>. Load-bearing
    ///     for shift detection.
    /// </summary>
    public required double BotProbability { get; init; }

    /// <summary>Detection confidence at write time. Used to weight the aggregate + drive shaped eviction.</summary>
    public required double Confidence { get; init; }

    /// <summary>Response status code (from upstream when <see cref="FromUpstream"/>, else stylobot's own enforcement code).</summary>
    public required int StatusCode { get; init; }

    /// <summary>
    ///     Closed-loop feedback gate (see <c>project_centroid_learning_feedback_loop</c>):
    ///     <c>true</c> when <see cref="StatusCode"/> came from upstream,
    ///     <c>false</c> when stylobot itself set it (load-shed 503, policy
    ///     block 403, throttle 429, honeypot 404). The session atom uses
    ///     this to segment natural-visitor status codes from stylobot's own
    ///     enforcement responses when deciding shift.
    /// </summary>
    public required bool FromUpstream { get; init; }

    /// <summary>Was this response a honeypot hit? Load-bearing shift signal (always meaningful, even during outage).</summary>
    public required bool Honeypot { get; init; }

    /// <summary>Request path (or null when omitted for PII). Used only for observability, not shift detection.</summary>
    public string? Path { get; init; }

    /// <summary>
    ///     Inferred client type at write time (e.g. "browser", "scraper",
    ///     "search-engine-bot"). Shift detection tracks whether the dominant
    ///     inferred type changes across samples.
    /// </summary>
    public string? ClientType { get; init; }

    /// <summary>
    ///     Correlator for observability. Not aggregated. Optional so
    ///     summary escalators without a per-request trace can still emit.
    /// </summary>
    public string? RequestId { get; init; }
}