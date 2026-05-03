using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Orchestration.Audit;

public sealed class ErrorSignalAuditProcessor : IAuditProcessor
{
    private readonly ErrorSignalAuditProcessorOptions _options;

    public ErrorSignalAuditProcessor(IOptions<AuditProcessorOptions> options)
    {
        _options = options.Value.Errors;
    }

    public string Name => "ErrorSignalAuditProcessor";

    public async ValueTask ProcessAsync(
        AuditProcessingContext context,
        IAuditRecordWriter writer,
        CancellationToken ct = default)
    {
        if (!_options.Enabled) return;

        var matchingSignals = context.Signals
            .Where(kvp => Matches(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        if (matchingSignals.Count == 0) return;

        var detectorDeltas = context.Contributions
            .GroupBy(c => c.DetectorName)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(c => c.ConfidenceDelta * c.Weight),
                StringComparer.OrdinalIgnoreCase);

        var reasons = context.Contributions
            .Select(c => c.Reason)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList()!;

        var record = new AuditRecord
        {
            Type = "stylobot.audit.error",
            SourceProcessor = Name,
            Timestamp = context.Metadata.Timestamp,
            RequestId = context.Metadata.RequestId,
            PrimarySignature = context.Metadata.PrimarySignature,
            Path = context.Metadata.Path,
            Method = context.Metadata.Method,
            Action = context.Metadata.Action,
            RiskBand = context.Metadata.RiskBand,
            BotProbability = context.Metadata.BotProbability,
            Confidence = context.Metadata.Confidence,
            Severity = _options.MinimumSeverity,
            Signals = matchingSignals,
            DetectorDeltas = detectorDeltas,
            Reasons = reasons,
            Properties = new Dictionary<string, object>
            {
                ["policyName"] = context.Metadata.PolicyName ?? "",
                ["statusCode"] = context.Metadata.StatusCode ?? 0,
                ["processingTimeMs"] = context.Metadata.ProcessingTimeMs
            }
        };

        await writer.WriteAsync(record, ct);
    }

    private bool Matches(string key)
        => _options.SignalPrefixes.Any(prefix =>
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
