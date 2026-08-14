using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Structured dashboard state for the /internal/diagnostics surface (operator
///     directive 2026-08-13): the structured answer to "why is this page empty" — the
///     materializer's cadence + warm coverage, per-target stash state, feed health, and
///     poison-guard refusals, without a vision pass. Read-only; hosted keyed on the
///     commercial website. The singleton collects the counters (poison refusals + the
///     compose-feed ring) and snapshots the cache state on demand.
/// </summary>
public sealed class DashboardDiagnostics
{
    private int _poisonRefusals;
    private long _lastTickTicksUtc;
    private int _tickCount;
    private readonly ConcurrentQueue<(DateTimeOffset At, string Kind, int? Status, double Ms)> _feed = new();

    /// <summary>Compose-failure bundles refused by the composer's guard (never cached).</summary>
    public int PoisonRefusals => Volatile.Read(ref _poisonRefusals);

    public void RecordPoisonRefusal() => Interlocked.Increment(ref _poisonRefusals);

    public void RecordTick(DateTimeOffset now)
    {
        Interlocked.Exchange(ref _lastTickTicksUtc, now.UtcTicks);
        Interlocked.Increment(ref _tickCount);
    }

    public DateTimeOffset? LastTick => Interlocked.Read(ref _lastTickTicksUtc) == 0
        ? null
        : new DateTimeOffset(Interlocked.Read(ref _lastTickTicksUtc), TimeSpan.Zero);

    public int TickCount => Volatile.Read(ref _tickCount);

    // Ticks whose pass aborted before any warm (the silent-abort class: the queue build
    // throws → no warm, no feed, ticks advance — the "0/30 cold, feedHistory 0" staging
    // signature). The counter + the last failure message make the abort visible in
    // /internal/diagnostics/state instead of a silent void.
    private long _tickFailures;
    private string _lastTickFailure = "";

    public void RecordTickFailure(Exception ex)
    {
        Interlocked.Increment(ref _tickFailures);
        Volatile.Write(ref _lastTickFailure, $"{ex.GetType().Name}: {ex.Message}".Length > 300
            ? $"{ex.GetType().Name}: {ex.Message}"[..300]
            : $"{ex.GetType().Name}: {ex.Message}");
    }

    public long TickFailures => Volatile.Read(ref _tickFailures);
    public string LastTickFailure => Volatile.Read(ref _lastTickFailure);

    // The warm queue's per-tick state (the silent-empty signature's visibility): the last
    // tick's queue-empty flag + the pinned-eligible + live counts. Exposed in
    // /internal/diagnostics/state so a next-tick probe names WHY the queue was empty.
    private long _queueEmpty;
    private int _lastPinnedEligible;
    private int _lastLive;

    public void RecordQueueState(bool queueEmpty, int pinnedEligible, int live)
    {
        if (queueEmpty) Interlocked.Increment(ref _queueEmpty);
        Volatile.Write(ref _lastPinnedEligible, pinnedEligible);
        Volatile.Write(ref _lastLive, live);
    }

    public long QueueEmptyTicks => Volatile.Read(ref _queueEmpty);
    public int LastPinnedEligible => Volatile.Read(ref _lastPinnedEligible);
    public int LastLive => Volatile.Read(ref _lastLive);

    /// <summary>Records one compose-batch result (the website's remote feed).</summary>
    public void RecordFeed(string kind, int? statusCode, double latencyMs)
    {
        _feed.Enqueue((DateTimeOffset.UtcNow, kind, statusCode, latencyMs));
        while (_feed.Count > 100) _feed.TryDequeue(out _);
    }

    public IReadOnlyList<(DateTimeOffset At, string Kind, int? Status, double Ms)> FeedHistory =>
        _feed.ToArray();

    /// <summary>
    ///     Snapshot of the per-target stash state: for every pinned page manifest × window
    ///     token, the envelope key, whether the content cache holds a warm bundle for it
    ///     (at the current change-cursor tick), and the shingle count.
    /// </summary>
    public IReadOnlyList<TargetState> SnapshotTargets(
        IDashboardContentCache cache,
        IDashboardPageManifestSource manifests,
        IOptions<DashboardMaterializerOptions> materializerOptions,
        IDashboardChangeCursor cursor,
        DashboardWidgetShingleCache shingles,
        DateTime now)
    {
        var targets = new List<TargetState>();
        var pinnedKeys = materializerOptions.Value.PrewarmPageKeys.Count > 0
            ? materializerOptions.Value.PrewarmPageKeys
            : new[] { materializerOptions.Value.PrewarmPageKey };
        var tokens = materializerOptions.Value.PrewarmWindows;
        foreach (var pageKey in pinnedKeys)
        {
            if (manifests.For(pageKey) is not { } manifest) continue;
            foreach (var token in tokens)
            {
                var window = DashboardRoutingHelpers.BuildPinnedWindow(token, now);
                var envelope = DashboardContentEnvelope.From(manifest, window);
                var warm = cache.TryGet(manifest, window, cursor.CurrentTick, out var page);
                var shingleCount = shingles.Snapshot().Count;
                targets.Add(new TargetState(
                    PageKey: pageKey,
                    WindowToken: token,
                    EnvelopeKey: envelope.ToString(),
                    Warm: warm,
                    IsWarming: warm && page is { IsWarming: true },
                    LastComposedTick: warm ? cursor.CurrentTick : null,
                    ShingleCount: shingleCount));
            }
        }
        return targets;
    }
}

/// <summary>One pinned target's cache state.</summary>
public sealed record TargetState(
    string PageKey,
    string WindowToken,
    string EnvelopeKey,
    bool Warm,
    bool IsWarming,
    long? LastComposedTick,
    int ShingleCount);

/// <summary>Assigns the composer's static poison hook at first resolution (the DI site
/// that knows both the diagnostics singleton and the composer's static).</summary>
public interface IPostConfigureDashboardDiagnostics;

public sealed class DashboardDiagnosticsHook : IPostConfigureDashboardDiagnostics
{
    public DashboardDiagnosticsHook(DashboardDiagnostics diagnostics)
    {
        Composition.DefaultDashboardPageComposer.RecordPoisonRefusalHook = diagnostics.RecordPoisonRefusal;
    }
}
