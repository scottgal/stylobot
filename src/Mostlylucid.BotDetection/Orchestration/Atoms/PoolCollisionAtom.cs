using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that detects fingerprint-pool
///     collisions: the same narrow shape hash (canvas + WebGL vendor + renderer)
///     observed under N+ distinct <c>(ip-hash, session-id)</c> contexts within
///     a sliding window.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>PoolCollisionContributor</c>. Catches anti-detect browsers whose
///         curated real-fingerprint databases have finite cardinality --
///         Multilogin Mimic and Kameleo Chroma both distribute the same
///         profiles to multiple customers, so collisions are inevitable above
///         scrape volume. Real users are unique per device so the shape-hash
///         collision rate among legitimate traffic is effectively zero.
///     </para>
///     <para>
///         Runs in Wave 1 because it depends on
///         <see cref="SignalKeys.ClientSideShapeHash"/> being raised by
///         <c>ClientSideAtom</c>. Idempotent on repeat observations from the
///         same <c>(ip, session)</c> within the window (tracker dedups
///         internally).
///     </para>
/// </remarks>
public sealed class PoolCollisionAtom : DetectorAtomBase
{
    private readonly IFingerprintPoolCollisionTracker _tracker;
    private readonly ILogger<PoolCollisionAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PoolCollisionAtom(
        IFingerprintPoolCollisionTracker tracker,
        ILogger<PoolCollisionAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "PoolCollision", category: "PoolCollision")
    {
        _tracker = tracker;
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 55;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.ClientSideShapeHash };

    private TimeSpan Window =>
        TimeSpan.FromMinutes(_configProvider.GetParameter(Name, "window_minutes", 60.0));

    private int CollisionThreshold =>
        _configProvider.GetParameter(Name, "collision_threshold", 3);

    private double CollisionConfidence =>
        _configProvider.GetParameter(Name, "collision_confidence", 0.75);

    private double CollisionWeight =>
        _configProvider.GetParameter(Name, "collision_weight", 1.4);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // Model 2 hint reads: the sensor atom that raised these signals
        // embeds the value after a colon (e.g. "client_side.shape_hash:abc123").
        var shape = ReadHint(sink, SignalKeys.ClientSideShapeHash);
        if (string.IsNullOrEmpty(shape))
            return Task.FromResult(None());

        var ipHash = ReadHint(sink, SignalKeys.ClientIp) ?? "";

        // Connection.Id lives on HttpContext -- boundary read via accessor
        // is the correct shape until a session hydrator atom raises it.
        var context = _httpContextAccessor.HttpContext;
        var sessionKey = context?.Connection.Id ?? sessionId;
        var now = DateTimeOffset.UtcNow;

        // Query BEFORE observing so the current context isn't counted in
        // the "other contexts" total.
        var others = _tracker.DistinctContextsInWindow(shape, now - Window);
        _tracker.Observe(shape, ipHash, sessionKey, now);

        sink.Raise(
            $"{SignalKeys.ClientSidePoolCollisionContexts}:{others}",
            sessionId);

        if (others >= CollisionThreshold)
        {
            return Task.FromResult(Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = "PoolCollision",
                ConfidenceDelta = CollisionConfidence,
                Weight = CollisionWeight,
                Reason = $"Fingerprint pool collision: shape seen under {others} distinct contexts in the last {Window.TotalMinutes:F0}m (threshold {CollisionThreshold})",
                BotType = BotType.Scraper.ToString()
            }));
        }

        return Task.FromResult(None());
    }

    /// <summary>
    ///     Extract the value hint from a Model-2 signal like
    ///     <c>"prefix:value"</c>. Returns null if no signal with that prefix
    ///     was raised in the current window.
    /// </summary>
    private static string? ReadHint(SignalSink sink, string prefix)
    {
        var needle = prefix + ":";
        var signals = sink.Sense(s => s.Signal.StartsWith(needle, StringComparison.Ordinal));
        if (signals.Count == 0) return null;

        // Most recent hint wins (Model 2 hints may be stale, but for boundary
        // reads the freshest is the closest to source of truth).
        var latest = signals[0];
        for (var i = 1; i < signals.Count; i++)
        {
            if (signals[i].Timestamp > latest.Timestamp) latest = signals[i];
        }

        return latest.Signal[needle.Length..];
    }
}
