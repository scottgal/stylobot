using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Orchestration.Audit;

public sealed class AuditRecordWriter : IAuditRecordWriter
{
    private readonly IReadOnlyList<IAuditSink> _sinks;
    private readonly ILogger<AuditRecordWriter> _logger;

    public AuditRecordWriter(IEnumerable<IAuditSink> sinks, ILogger<AuditRecordWriter> logger)
    {
        _sinks = sinks.ToList();
        _logger = logger;
    }

    public async ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default)
    {
        if (_sinks.Count == 0) return;

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.WriteAsync(record, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Request is completing/cancelled. Do not turn audit cancellation into request failure.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Audit sink {SinkName} failed to write {AuditType} for {RequestId}",
                    sink.Name,
                    record.Type,
                    record.RequestId);
            }
        }
    }
}
