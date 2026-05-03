namespace Mostlylucid.BotDetection.Orchestration.Audit;

/// <summary>
///     Extracts purpose-specific audit records from a completed detection trace.
/// </summary>
public interface IAuditProcessor
{
    string Name { get; }

    ValueTask ProcessAsync(
        AuditProcessingContext context,
        IAuditRecordWriter writer,
        CancellationToken ct = default);
}
