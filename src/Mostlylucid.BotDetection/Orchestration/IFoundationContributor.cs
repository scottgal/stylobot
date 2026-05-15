namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Marker for contributors that establish identity context for the request before any
///     classifier weighs in. The orchestrator runs every <see cref="IFoundationContributor"/>
///     unconditionally; <see cref="Policies.DetectionPolicy"/> filters classifiers only.
///     Identity is never optional.
///
///     Two sub-shapes both qualify (Compute and Match). For categorisation rules and the
///     historical regression that motivated this split, see
///     <c>docs/architecture/signal-contracts.md</c>.
/// </summary>
public interface IFoundationContributor : IContributingDetector;