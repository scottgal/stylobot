using System.Net;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.Common.Scheduling;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Data;

namespace Stylobot.Gateway.Services;

/// <summary>
///     Drains the <see cref="ProfileAnalysisChannel"/> on a ScheduleCoordinator
///     tick and runs the FOSS detection pipeline against each captured request
///     snapshot to produce a <see cref="ProfileCalibrationEntry"/>.
///     <para>
///         <b>Wave 2 architectural-drift remediation (Category B).</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///         <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c> on the
///         channel reader and fanned each snapshot off onto a thread-pool
///         task gated by a per-instance <see cref="SemaphoreSlim"/>. Now
///         subscribes to <see cref="TickCadence.Tick10s"/> via
///         <see cref="IScheduleCoordinator"/>; each tick drains whatever
///         landed since the last tick via
///         <see cref="ProfileAnalysisChannel.TryRead"/> and runs the
///         detection pipeline per snapshot. The per-snapshot work is bounded
///         by a per-instance semaphore (B7: instance field, not method-local)
///         so the bounded SQLite write path stays bounded under burst load.
///         A 10-second tick cadence matches the natural calibration-capture
///         pace -- profile mode is an operator-driven feature, not a
///         hot-path detector.
///     </para>
///     <para>
///         The channel + producer code (<see cref="ProfileCaptureMiddleware"/>)
///         is untouched -- only the consumer changes shape.
///     </para>
/// </summary>
public sealed class ProfileAnalysisWorker : IDisposable
{
    private const string PolicyName = "yarp-learning";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProfileAnalysisChannel _channel;
    private readonly ProfileCalibrationStore _store;
    private readonly IOptions<ProfileModeOptions> _options;
    private readonly IOptions<BotDetectionOptions> _detectionOptions;
    private readonly ILogger<ProfileAnalysisWorker> _logger;
    private readonly SemaphoreSlim _semaphore;
    private readonly IDisposable? _subscription;
    private int _disposed;

    public ProfileAnalysisWorker(
        IServiceScopeFactory scopeFactory,
        ProfileAnalysisChannel channel,
        ProfileCalibrationStore store,
        IOptions<ProfileModeOptions> options,
        IOptions<BotDetectionOptions> detectionOptions,
        ILogger<ProfileAnalysisWorker> logger,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _scopeFactory = scopeFactory;
        _channel = channel;
        _store = store;
        _options = options;
        _detectionOptions = detectionOptions;
        _logger = logger;

        // B7: semaphore lives on the instance so the bound survives across
        // ticks (otherwise burst-y arrivals could pile up between ticks).
        var concurrency = Math.Max(1, _options.Value.Concurrency);
        _semaphore = new SemaphoreSlim(concurrency, concurrency);

        // Optional so existing direct-construction tests + test rigs that
        // exercise the channel/store paths without a coordinator keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "ProfileAnalysisWorker",
                CostHint.Medium,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: drain whatever profile
    ///     snapshots landed since the last tick via
    ///     <see cref="ProfileAnalysisChannel.TryRead"/> and dispatch detection +
    ///     persistence per snapshot under the per-instance concurrency cap.
    ///     Returns once the channel is empty -- the next tick handles whatever
    ///     arrived in between.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        if (!_options.Value.Enabled) return;

        var inflight = new List<Task>();

        while (_channel.TryRead(out var snapshot))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await _semaphore.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            inflight.Add(Task.Run(async () =>
            {
                try { await ProcessSnapshotAsync(snapshot, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Profile analysis failed for {RequestId}", snapshot.RequestId);
                }
                finally { _semaphore.Release(); }
            }, ct));
        }

        // Wait for all in-flight tasks to finish so the next tick sees a
        // settled state. ScheduleCoordinator's single-flight guarantee blocks
        // overlapping ticks regardless, but waiting here keeps the tick body
        // self-contained.
        if (inflight.Count > 0)
        {
            try { await Task.WhenAll(inflight); }
            catch
            {
                // Per-task exceptions are already logged inside the Task.Run
                // continuation; WhenAll re-raises the first one so we swallow
                // it here to keep one bad snapshot from poisoning the tick.
            }
        }
    }

    private async Task ProcessSnapshotAsync(ProfileRequestSnapshot snapshot, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var ctx = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        ctx.Request.Method = snapshot.Method;
        ctx.Request.Path = new PathString(snapshot.Path);
        ctx.Connection.RemoteIpAddress =
            IPAddress.TryParse(snapshot.ClientIp, out var ip) ? ip : null;

        foreach (var (key, values) in snapshot.Headers)
            ctx.Request.Headers[key] = values;

        if (snapshot.TlsProtocol != null) ctx.Items[GatewayHttpContextKeys.TlsProtocol] = snapshot.TlsProtocol;
        if (snapshot.TlsCipherSuite != null) ctx.Items[GatewayHttpContextKeys.TlsCipherSuite] = snapshot.TlsCipherSuite;

        var evidence = await RunDetectionAsync(ctx, scope.ServiceProvider, ct);

        var probability = evidence?.BotProbability ?? 0.0;
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
        var botThreshold = _detectionOptions.Value.BotThreshold;
#pragma warning restore CS0618
        var isBot = probability >= botThreshold;
        var botType = evidence?.PrimaryBotType?.ToString();
        var botName = evidence?.PrimaryBotName;

        var riskBand = probability switch
        {
            < 0.3 => "Low",
            < 0.5 => "Medium",
            < 0.7 => "High",
            _ => "VeryHigh",
        };

        var signatureHash = (evidence is null ? null : SignatureLookup.Primary(evidence))
            ?? ComputeVisitorHash(snapshot.ClientIp, snapshot.UserAgent);

        await _store.InsertAsync(new ProfileCalibrationEntry
        {
            SignatureHash = signatureHash,
            BotProbability = probability,
            RiskBand = riskBand,
            BotType = isBot ? botType : null,
            BotName = botName,
            TopDetector = null,
            PathPattern = PathNormalizer.Normalize(snapshot.Path),
        }, ct);
    }

    private async Task<AggregatedEvidence?> RunDetectionAsync(
        HttpContext ctx, IServiceProvider services, CancellationToken ct)
    {
        var orchestrator = services.GetService<BlackboardOrchestrator>();
        if (orchestrator is null)
        {
            _logger.LogWarning("BlackboardOrchestrator not available in profile analysis scope");
            return null;
        }

        return await orchestrator.DetectAsync(ctx, PolicyName, ct);
    }

    private static string ComputeVisitorHash(string ip, string ua)
    {
        var input = $"{ip}:{ua}";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
        try { _semaphore.Dispose(); }
        catch { /* already disposed */ }
    }
}