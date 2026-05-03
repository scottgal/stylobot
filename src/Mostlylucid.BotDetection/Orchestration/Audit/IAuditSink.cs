namespace Mostlylucid.BotDetection.Orchestration.Audit;

/// <summary>
///     Writes shaped audit records to a destination.
/// </summary>
public interface IAuditSink
{
    string Name { get; }

    ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default);
}

/// <summary>
///     Processor-facing writer that fans records out to configured sinks.
/// </summary>
public interface IAuditRecordWriter
{
    ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default);
}
