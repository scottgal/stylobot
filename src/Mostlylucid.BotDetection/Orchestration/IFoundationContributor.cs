namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Marker interface for contributors that compute identity / foundation facts the
///     rest of the system reads as truth (signature.primary, transport.protocol_class,
///     fingerprint.prior_probability, sequence.*, etc.).
///
///     Foundation contributors are <strong>not subject to policy filtering</strong>. The
///     orchestrator runs every <see cref="IFoundationContributor"/> unconditionally before
///     any classifier. Policy decides which classifiers to run; identity is never optional.
///
///     The historical bug shape this fixes: <see cref="SignatureContributor"/> was a regular
///     <see cref="IContributingDetector"/>, so <see cref="Policies.DetectionPolicy.Default"/>
///     could omit it from <c>FastPathDetectors</c>. The signal it writes
///     (<c>signature.primary</c>) is consumed by ~20 downstream surfaces (persistence,
///     dashboard fingerprint table, deterministic name synthesizer, prior-probability delta).
///     Default omitted Signature; Demo ran every contributor; tests ran Demo. The breakage
///     only surfaced in production.
///
///     <strong>What qualifies as foundation:</strong> the contributor writes a fact
///     identifying or contextualising the request that other contributors or display
///     consumers cannot synthesise themselves. Examples in this codebase:
///     <c>Signature</c>, <c>TransportProtocol</c>, <c>FingerprintPrior</c>,
///     <c>ContentSequence</c>, <c>FingerprintApproval</c>, <c>ChallengeVerification</c>,
///     <c>PiiQueryString</c>.
///
///     <strong>What does NOT qualify:</strong> classifiers that compute a probability
///     contribution (UserAgent, Header, Behavioral, etc.). Those are policy-gated by design,
///     because a tight policy may want to skip expensive analysis on cheap traffic.
/// </summary>
public interface IFoundationContributor : IContributingDetector;
