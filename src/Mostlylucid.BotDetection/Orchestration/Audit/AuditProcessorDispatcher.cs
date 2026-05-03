using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Audit;

public sealed class AuditProcessorDispatcher
{
    private readonly IReadOnlyList<IAuditProcessor> _processors;
    private readonly IAuditRecordWriter _writer;
    private readonly AuditProcessorOptions _options;
    private readonly ILogger<AuditProcessorDispatcher> _logger;

    public AuditProcessorDispatcher(
        IEnumerable<IAuditProcessor> processors,
        IAuditRecordWriter writer,
        IOptions<AuditProcessorOptions> options,
        ILogger<AuditProcessorDispatcher> logger)
    {
        _processors = processors.ToList();
        _writer = writer;
        _options = options.Value;
        _logger = logger;
    }

    public bool HasProcessors => _options.Enabled && _processors.Count > 0;

    public async ValueTask DispatchAsync(
        HttpContext httpContext,
        AggregatedEvidence evidence,
        CancellationToken ct = default)
    {
        if (!HasProcessors) return;

        var context = BuildContext(httpContext, evidence);
        // Fix up StatusCode: response is already finalized when DispatchAsync is called inline
        context = context with
        {
            Metadata = context.Metadata with { StatusCode = httpContext.Response.StatusCode }
        };
        await DispatchPrebuiltAsync(context, ct);
    }

    /// <summary>
    ///     Dispatches a pre-built audit context. Safe to call fire-and-forget
    ///     because the context holds no HttpContext reference.
    /// </summary>
    public async ValueTask DispatchPrebuiltAsync(
        AuditProcessingContext context,
        CancellationToken ct = default)
    {
        if (!HasProcessors) return;

        foreach (var processor in _processors)
        {
            try
            {
                await processor.ProcessAsync(context, _writer, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Audit processor {ProcessorName} failed for {RequestId}",
                    processor.Name,
                    context.Metadata.RequestId);
            }
        }
    }

    public AuditProcessingContext BuildContext(HttpContext httpContext, AggregatedEvidence evidence)
    {
        var signals = RetainSignals(evidence.Signals);

        return new AuditProcessingContext
        {
            HttpContext = null,   // do not hold a reference - HttpContext is pooled
            Evidence = evidence,
            Signals = signals,
            Contributions = evidence.Contributions,
            Metadata = new AuditTraceMetadata
            {
                Timestamp = DateTime.UtcNow,
                RequestId = httpContext.TraceIdentifier,
                PrimarySignature = TryGetPrimarySignature(httpContext),
                Path = httpContext.Request.Path.Value,
                Method = httpContext.Request.Method,
                StatusCode = null,  // set by caller after response is finalized
                PolicyName = evidence.PolicyName,
                Action = evidence.TriggeredActionPolicyName ?? evidence.PolicyAction?.ToString(),
                RiskBand = evidence.RiskBand.ToString(),
                BotProbability = evidence.BotProbability,
                Confidence = evidence.Confidence,
                ProcessingTimeMs = evidence.TotalProcessingTimeMs
            }
        };
    }

    private IReadOnlyDictionary<string, object> RetainSignals(IReadOnlyDictionary<string, object> source)
    {
        if (!_options.SignalRetention.Enabled || source.Count == 0)
            return new Dictionary<string, object>();

        var retained = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (retained.Count >= _options.SignalRetention.MaxSignalCount) break;
            if (IsExcluded(key)) continue;
            if (!_options.SignalRetention.RetainAllSignals && !IsRetained(key)) continue;
            retained[key] = value;
        }

        return retained;
    }

    private bool IsRetained(string key)
        => _options.SignalRetention.RetainedSignalPrefixes.Any(prefix =>
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private bool IsExcluded(string key)
        => _options.SignalRetention.ExcludedSignalPrefixes.Any(prefix =>
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string? TryGetPrimarySignature(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue("BotDetection:Signature", out var signature) &&
            signature is string primarySignature &&
            !string.IsNullOrWhiteSpace(primarySignature))
            return primarySignature;

        if (httpContext.Items.TryGetValue("BotDetection.Signatures", out var signatures) &&
            signatures is MultiFactorSignatures multiFactorSignatures &&
            !string.IsNullOrWhiteSpace(multiFactorSignatures.PrimarySignature))
            return multiFactorSignatures.PrimarySignature;

        if (httpContext.Items.TryGetValue("BotDetection.PrimarySignature", out var primary) &&
            primary is string legacyPrimary &&
            !string.IsNullOrWhiteSpace(legacyPrimary))
            return legacyPrimary;

        return null;
    }
}
