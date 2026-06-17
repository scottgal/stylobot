using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration.Telemetry;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Top-level alias for the signature factor tuple that is captured per
///     request before enqueue. <c>DetectionBroadcastMiddleware</c> keeps its own
///     private record + a <c>ToShared()</c> bridge so the file boundary stays
///     clean; both reference this same shape.
/// </summary>
public readonly record struct SignatureFactors(string? IpSig, string? UaSig, string? ClientSig, int FactorCount);

/// <summary>
///     Dashboard-domain persistence coordinator. Wraps the framework primitive
///     <see cref="EphemeralWorkCoordinator{T}"/> (Mostlylucid.Ephemeral) so the
///     request hot path only does a sync <see cref="Enqueue"/> and never touches
///     <c>Task.Run</c>, <c>IHostedService</c>, or its own timer.
///     <para>
///     <b>Why this exists.</b> Until now, <see cref="DetectionBroadcastMiddleware"/>
///     used <c>_ = Task.Run(async () =&gt; await PersistAsync(…))</c> per request.
///     That is the wrong pattern: every fire-and-forget task is invisible to the
///     coordinator, has no concurrency control, no back-pressure, and -- on a
///     gateway that the supervisor restarts -- gets killed mid-flight before
///     it can complete its first await. Result on staging: zero rows in
///     <c>dashboard_detections</c> for three days.
///     </para>
///     <para>
///     <b>Why <see cref="EphemeralWorkCoordinator{T}"/>.</b> It is the project's
///     canonical coordination primitive: a long-lived processing loop owned by
///     the framework, fed by a bounded internal channel, with configurable
///     concurrency for storm-prevention. The TaskCompletionSource-based drain
///     contract is the same one signal-based orchestration uses everywhere
///     else. Items enqueued are observable (<c>PendingCount</c>, <c>ActiveCount</c>,
///     <c>TotalCompleted</c>) instead of vanishing into a fire-and-forget task.
///     </para>
///     <para>
///     <b>Separate sink, separate concern.</b> This coordinator is dedicated to
///     dashboard persistence (detection + signature rows + dashboard event
///     publishers + signature-description tracking). Other concerns (learning,
///     reputation, descriptions, threat intel) get their own coordinators sized
///     for their own write characteristics.
///     </para>
/// </summary>
public sealed class DashboardPersistCoordinator : IAsyncDisposable
{
    private readonly EphemeralWorkCoordinator<PendingPersist> _coordinator;
    private readonly IDashboardEventStore _store;
    private readonly IDetectionEventPublisher? _publisher;
    private readonly SignatureDescriptionService? _sigDescService;
    private readonly ILogger<DashboardPersistCoordinator> _logger;
    private int _disposed;

    public DashboardPersistCoordinator(
        IDashboardEventStore store,
        IDetectionEventPublisher? publisher,
        SignatureDescriptionService? sigDescService,
        ILogger<DashboardPersistCoordinator> logger,
        IOptions<DashboardPersistCoordinatorOptions>? optionsAccessor = null)
    {
        var options = optionsAccessor?.Value ?? new DashboardPersistCoordinatorOptions();
        _store = store;
        _publisher = publisher;
        _sigDescService = sigDescService;
        _logger = logger;

        _coordinator = new EphemeralWorkCoordinator<PendingPersist>(
            body: WriteOneAsync,
            options: new EphemeralOptions
            {
                MaxConcurrency = options.MaxConcurrency,
                MaxTrackedOperations = options.MaxTrackedOperations
            });
    }

    /// <summary>Diagnostic counters (passthrough from the framework primitive).</summary>
    public int PendingCount   => _coordinator.PendingCount;
    public int ActiveCount    => _coordinator.ActiveCount;
    public int TotalEnqueued  => _coordinator.TotalEnqueued;
    public int TotalCompleted => _coordinator.TotalCompleted;
    public int TotalFailed    => _coordinator.TotalFailed;

    /// <summary>
    ///     Synchronous, non-blocking enqueue. Returns immediately; the framework
    ///     coordinator drains the internal channel in turn (default
    ///     MaxConcurrency=1 — strictly one write at a time, no storms).
    /// </summary>
    public void Enqueue(
        DashboardDetectionEvent detection,
        SignatureFactors factors,
        IReadOnlyDictionary<string, object>? signals)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // TryEnqueue is fire-and-forget on success/failure for back-pressure
        // semantics; the in-memory cache layers (SignatureAggregateCache /
        // VisitorListCache) are the read source of truth, so a dropped queue
        // entry only loses one durable row.
        _coordinator.TryEnqueue(new PendingPersist(detection, factors, signals));
    }

    private async Task WriteOneAsync(PendingPersist item, CancellationToken ct)
    {
        try
        {
            await _store.AddDetectionAsync(item.Detection);

            var signature = new DashboardSignatureEvent
            {
                SignatureId = Guid.NewGuid().ToString("N")[..12],
                Timestamp = DateTime.UtcNow,
                PrimarySignature = item.Detection.PrimarySignature ?? item.Detection.RequestId,
                IpSignature = item.Factors.IpSig,
                UaSignature = item.Factors.UaSig,
                ClientSideSignature = item.Factors.ClientSig,
                FactorCount = item.Factors.FactorCount,
                RiskBand = item.Detection.RiskBand,
                HitCount = 1,
                IsKnownBot = item.Detection.IsBot,
                BotName = item.Detection.BotName,
                BotProbability = item.Detection.BotProbability,
                Confidence = item.Detection.Confidence,
                ProcessingTimeMs = item.Detection.ProcessingTimeMs,
                BotType = item.Detection.BotType,
                Action = item.Detection.Action,
                LastPath = item.Detection.Path,
                Narrative = item.Detection.Narrative,
                Description = item.Detection.Description,
                TopReasons = item.Detection.TopReasons?.ToList(),
                ThreatScore = item.Detection.ThreatScore,
                ThreatBand = item.Detection.ThreatBand,
                RiskJustification = item.Detection.RiskJustification,
            };
            await _store.AddSignatureAsync(signature);

            if (_publisher is not null)
            {
                try
                {
                    // Project the DashboardDetectionEvent into the transport DTO
                    // the publisher contract expects (DetectionEvent in
                    // Mostlylucid.BotDetection.Orchestration.Telemetry).
                    Dictionary<string, double>? detectorContribs = null;
                    if (item.Detection.DetectorContributions is { Count: > 0 } dc)
                    {
                        detectorContribs = new Dictionary<string, double>(dc.Count, StringComparer.Ordinal);
                        foreach (var kv in dc)
                            detectorContribs[kv.Key] = kv.Value.Contribution;
                    }

                    var evt = new Mostlylucid.BotDetection.Orchestration.Telemetry.DetectionEvent
                    {
                        Timestamp = item.Detection.Timestamp,
                        RequestId = item.Detection.RequestId ?? string.Empty,
                        Signature = item.Detection.PrimarySignature ?? "",
                        Path = item.Detection.Path,
                        Method = item.Detection.Method,
                        StatusCode = item.Detection.StatusCode,
                        IsBot = item.Detection.IsBot,
                        BotProbability = item.Detection.BotProbability,
                        Confidence = item.Detection.Confidence,
                        RiskBand = item.Detection.RiskBand,
                        ThreatBand = item.Detection.ThreatBand,
                        Action = item.Detection.Action,
                        BotName = item.Detection.BotName,
                        BotType = item.Detection.BotType,
                        CountryCode = item.Detection.CountryCode,
                        ProcessingTimeMs = item.Detection.ProcessingTimeMs,
                        DetectorContributions = detectorContribs,
                        TopReasons = item.Detection.TopReasons,
                        GatewayId = Environment.GetEnvironmentVariable("STYLOBOT_GATEWAY_ID")
                                    ?? Environment.MachineName
                    };
                    await _publisher.PublishAsync(evt, ct);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Detection event publish failed"); }
            }

            if (_sigDescService is not null
                && !string.IsNullOrEmpty(item.Detection.PrimarySignature)
                && item.Signals is { Count: > 0 })
            {
                try
                {
                    var nullableSignals = item.Signals.ToDictionary(s => s.Key, s => (object?)s.Value);
                    _sigDescService.TrackSignature(item.Detection.PrimarySignature, nullableSignals);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "TrackSignature failed"); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "DashboardPersistCoordinator write failed: sig={Sig} path={Path}",
                item.Detection.PrimarySignature?[..Math.Min(8, item.Detection.PrimarySignature.Length)],
                item.Detection.Path);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    internal readonly record struct PendingPersist(
        DashboardDetectionEvent Detection,
        SignatureFactors Factors,
        IReadOnlyDictionary<string, object>? Signals);
}

/// <summary>Tuning knobs for <see cref="DashboardPersistCoordinator"/>.</summary>
public sealed class DashboardPersistCoordinatorOptions
{
    /// <summary>
    ///     How many persists run in parallel. Default 1: strictly one DB write
    ///     at a time — the "each updates in turn" guarantee that prevents
    ///     write storms and keeps the connection pool from being held by
    ///     dozens of concurrent in-flight inserts. Raise carefully if Postgres
    ///     can take the parallelism.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    ///     Internal bounded-channel capacity (framework owns the channel,
    ///     this just sizes it). When full, the framework applies back-pressure
    ///     per its bounded-channel default mode. Sized for ~250 RPS sustained:
    ///     250 × 40 sec = 10 000 with headroom for transient spikes.
    /// </summary>
    public int MaxTrackedOperations { get; set; } = 10_000;
}