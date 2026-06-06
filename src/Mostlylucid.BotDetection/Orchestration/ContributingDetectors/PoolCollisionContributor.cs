using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Detects fingerprint-pool collisions: the same narrow shape hash
///     (canvas + WebGL vendor + renderer) observed under N+ distinct
///     (IP-hash, session-id) contexts within a sliding window.
///     <para>
///     Catches anti-detect browsers whose curated real-fingerprint
///     databases have finite cardinality -- Multilogin Mimic and Kameleo
///     Chroma both distribute the same profiles to multiple customers, so
///     collisions are inevitable above scrape volume. Real users are
///     unique per device so the shape-hash collision rate among legitimate
///     traffic is effectively zero.
///     </para>
///     <para>
///     Runs in Wave 1 because it depends on
///     <see cref="SignalKeys.ClientSideShapeHash"/> being written by
///     <see cref="ClientSideContributor"/>. Idempotent on repeat
///     observations from the same (ip, session) within the window (the
///     tracker dedups internally).
///     </para>
/// </summary>
public sealed class PoolCollisionContributor : ConfiguredContributorBase
{
    private readonly IFingerprintPoolCollisionTracker _tracker;
    private readonly ILogger<PoolCollisionContributor> _logger;

    public PoolCollisionContributor(
        IFingerprintPoolCollisionTracker tracker,
        ILogger<PoolCollisionContributor> logger,
        IDetectorConfigProvider configProvider) : base(configProvider)
    {
        _tracker = tracker;
        _logger = logger;
    }

    public override string Name => "PoolCollision";
    public override int Priority => Manifest?.Priority ?? 55;

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.WhenSignalExists(SignalKeys.ClientSideShapeHash)
    ];

    private TimeSpan Window => TimeSpan.FromMinutes(GetParam("window_minutes", 60.0));
    private int CollisionThreshold => GetParam("collision_threshold", 3);
    private double CollisionConfidence => GetParam("collision_confidence", 0.75);
    private double CollisionWeight => GetParam("collision_weight", 1.4);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        var shape = state.GetSignal<string>(SignalKeys.ClientSideShapeHash);
        if (string.IsNullOrEmpty(shape)) return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        var ipHash = state.GetSignal<string>(SignalKeys.ClientIp) ?? "";
        var sessionId = state.HttpContext.Connection.Id ?? state.RequestId;
        var now = DateTimeOffset.UtcNow;

        // Query before observing so the current context isn't counted in
        // the "other contexts" total. The signal value reflects how many
        // OTHER (ip, session) combos have seen this shape recently.
        var others = _tracker.DistinctContextsInWindow(shape, now - Window);
        _tracker.Observe(shape, ipHash, sessionId, now);

        state.WriteSignal(SignalKeys.ClientSidePoolCollisionContexts, others);

        if (others >= CollisionThreshold)
        {
            contributions.Add(BotContribution(
                "PoolCollision",
                $"Fingerprint pool collision: shape seen under {others} distinct contexts in the last {Window.TotalMinutes:F0}m (threshold {CollisionThreshold})",
                confidenceOverride: CollisionConfidence,
                weightMultiplier: CollisionWeight,
                botType: BotType.Scraper.ToString()));
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }
}
