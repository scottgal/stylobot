using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.AspNetPack.Configuration;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.AspNetPack.Logging;

/// <summary>
///     Drains <see cref="StyloBotGatewayLoggerProvider.Channel"/> in bounded batches and
///     hands each batch to <see cref="StyloBotGatewayLogExporter"/> for OTLP/logs dispatch.
///     Subscribes to <see cref="TickCadence.Tick10s"/> matching the
///     <see cref="LogSinkOptions.FlushTick"/> default; each tick drains up to
///     <see cref="LogSinkOptions.BatchSize"/> records non-blocking via
///     <see cref="System.Threading.Channels.ChannelReader{T}.TryRead"/>.
///     <see cref="DrainOnceAsync"/> is internal so tests can drive it directly without
///     spinning the hosted loop.
/// </summary>
public sealed class LogSinkDrainer : IDisposable
{
    private readonly StyloBotGatewayLoggerProvider _provider;
    private readonly StyloBotGatewayLogExporter _exporter;
    private readonly IOptions<LogSinkOptions> _opts;
    private readonly ILogger<LogSinkDrainer> _log;
    private readonly ILogSinkInstrumentation _instrumentation;
    private readonly IDisposable? _subscription;
    private int _disposed;

    public LogSinkDrainer(
        StyloBotGatewayLoggerProvider provider,
        StyloBotGatewayLogExporter exporter,
        IOptions<LogSinkOptions> opts,
        ILogger<LogSinkDrainer> log,
        IScheduleCoordinator? scheduleCoordinator = null,
        ILogSinkInstrumentation? instrumentation = null)
    {
        _provider = provider;
        _exporter = exporter;
        _opts = opts;
        _log = log;
        _instrumentation = instrumentation ?? NoOpLogSinkInstrumentation.Instance;

        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "LogSinkDrainer",
                CostHint.Low,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: drain up to
    ///     <see cref="LogSinkOptions.BatchSize"/> log records from the
    ///     <see cref="StyloBotGatewayLoggerProvider.Channel"/> via
    ///     <see cref="System.Threading.Channels.ChannelReader{T}.TryRead"/>
    ///     (non-blocking) and POST the batch to the gateway.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        try
        {
            await DrainOnceAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "LogSinkDrainer tick failed");
        }
    }

    internal async Task DrainOnceAsync(CancellationToken ct)
    {
        var batchSize = _opts.Value.BatchSize;
        var reader = _provider.Channel.Reader;
        var batch = new List<LogRecord>(batchSize);

        while (batch.Count < batchSize && reader.TryRead(out var rec))
            batch.Add(rec);

        // Update queue depth gauge regardless of batch fullness so the
        // observability surface tracks backpressure even when the drainer
        // is keeping up. CanCount may be false on some channel impls; the
        // try/catch protects the export path from a faulty instrumentation
        // dependency taking down the tick.
        try
        {
            _instrumentation.SetQueueDepth(reader.CanCount ? reader.Count : 0);
        }
        catch { /* instrumentation must not break the drain */ }

        if (batch.Count == 0) return;

        var result = await _exporter.ExportAsync(batch, ct);
        switch (result.Outcome)
        {
            case ExportOutcome.Success:
                _instrumentation.RecordShipped("ok", batch.Count);
                break;
            case ExportOutcome.GatewayUnreachable:
                _instrumentation.RecordGatewayUnreachable();
                _instrumentation.RecordShipped("unreachable", batch.Count);
                break;
            case ExportOutcome.NonSuccess:
                _instrumentation.RecordShipped("fail", batch.Count);
                break;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}
