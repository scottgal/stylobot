using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Audit;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration.Audit;

public class AuditProcessorTests
{
    [Fact]
    public async Task DispatchPrebuiltAsync_WithNullHttpContext_ProcessorStillReceivesSignals()
    {
        var processor = new SnapshotProcessor();
        var sink = new CaptureSink();
        var dispatcher = CreateDispatcher(
            [processor],
            sink,
            new AuditProcessorOptions { Enabled = true });

        var ctx = new AuditProcessingContext
        {
            HttpContext = null,
            Evidence = CreateEvidence(signals: new Dictionary<string, object> { ["test.signal"] = 1 }),
            Signals = new Dictionary<string, object> { ["test.signal"] = 1 },
            Contributions = [],
            Metadata = new AuditTraceMetadata
            {
                Timestamp = DateTime.UtcNow,
                RequestId = "test-req-id",
                PrimarySignature = "sig123",
                Path = "/test",
                Method = "GET",
                StatusCode = 200,
                PolicyName = "default",
                Action = null,
                RiskBand = "low",
                BotProbability = 0.1,
                Confidence = 0.9,
                ProcessingTimeMs = 5.0
            }
        };
        await dispatcher.DispatchPrebuiltAsync(ctx);

        Assert.Equal(1, processor.InvocationCount);
    }

    [Fact]
    public async Task DispatchAsync_WhenDisabled_DoesNotInvokeProcessors()
    {
        var processor = new SnapshotProcessor();
        var sink = new CaptureSink();
        var dispatcher = CreateDispatcher(
            [processor],
            sink,
            new AuditProcessorOptions { Enabled = false });

        await dispatcher.DispatchAsync(CreateHttpContext(), CreateEvidence());

        Assert.Equal(0, processor.InvocationCount);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task DispatchAsync_RetainsConfiguredSignalsAndExcludesSensitiveSignals()
    {
        var sink = new CaptureSink();
        var dispatcher = CreateDispatcher(
            [new SnapshotProcessor()],
            sink,
            new AuditProcessorOptions
            {
                Enabled = true,
                SignalRetention = new AuditSignalRetentionOptions
                {
                    Enabled = true,
                    RetainAllSignals = true,
                    ExcludedSignalPrefixes = ["request.cookie", "ua.raw"],
                    MaxSignalCount = 10
                }
            });

        var evidence = CreateEvidence(signals: new Dictionary<string, object>
        {
            ["pipeline.error.message"] = "timeout",
            ["request.cookie.session"] = "secret",
            ["ua.raw"] = "Mozilla/5.0 private",
            ["custom.signal"] = 42
        });

        await dispatcher.DispatchAsync(CreateHttpContext(), evidence);

        var record = Assert.Single(sink.Records);
        Assert.NotNull(record.Signals);
        Assert.True(record.Signals.ContainsKey("pipeline.error.message"));
        Assert.True(record.Signals.ContainsKey("custom.signal"));
        Assert.False(record.Signals.ContainsKey("request.cookie.session"));
        Assert.False(record.Signals.ContainsKey("ua.raw"));
    }

    [Fact]
    public async Task DispatchAsync_WhenRetainAllSignalsFalse_UsesRetainedPrefixes()
    {
        var sink = new CaptureSink();
        var dispatcher = CreateDispatcher(
            [new SnapshotProcessor()],
            sink,
            new AuditProcessorOptions
            {
                Enabled = true,
                SignalRetention = new AuditSignalRetentionOptions
                {
                    Enabled = true,
                    RetainAllSignals = false,
                    RetainedSignalPrefixes = ["pipeline."],
                    ExcludedSignalPrefixes = [],
                    MaxSignalCount = 10
                }
            });

        var evidence = CreateEvidence(signals: new Dictionary<string, object>
        {
            ["pipeline.error.message"] = "timeout",
            ["detector.timeout.useragent"] = true
        });

        await dispatcher.DispatchAsync(CreateHttpContext(), evidence);

        var record = Assert.Single(sink.Records);
        Assert.NotNull(record.Signals);
        Assert.True(record.Signals.ContainsKey("pipeline.error.message"));
        Assert.False(record.Signals.ContainsKey("detector.timeout.useragent"));
    }

    [Fact]
    public async Task DispatchAsync_UsesActualPrimarySignature_NotSignaturesObjectToString()
    {
        var sink = new CaptureSink();
        var dispatcher = CreateDispatcher(
            [new SnapshotProcessor()],
            sink,
            new AuditProcessorOptions { Enabled = true });
        var context = CreateHttpContext();
        var evidence = CreateEvidence(signals: new Dictionary<string, object>
        {
            [SignalKeys.SignatureMultifactor] = new MultiFactorSignatures
            {
                PrimarySignature = "primary-hmac",
                IpSignature = "ip-hmac"
            }
        });

        await dispatcher.DispatchAsync(context, evidence);

        var record = Assert.Single(sink.Records);
        Assert.Equal("primary-hmac", record.PrimarySignature);
    }

    [Fact]
    public async Task ErrorSignalAuditProcessor_WritesErrorRecordWithTraceMetadata()
    {
        var sink = new CaptureSink();
        var processor = new ErrorSignalAuditProcessor(Options.Create(new AuditProcessorOptions
        {
            Errors = new ErrorSignalAuditProcessorOptions
            {
                Enabled = true,
                MinimumSeverity = "Error",
                SignalPrefixes = ["pipeline.error"]
            }
        }));
        var dispatcher = CreateDispatcher(
            [processor],
            sink,
            new AuditProcessorOptions
            {
                Enabled = true,
                SignalRetention = new AuditSignalRetentionOptions
                {
                    Enabled = true,
                    RetainAllSignals = true,
                    ExcludedSignalPrefixes = [],
                    MaxSignalCount = 10
                }
            });
        var context = CreateHttpContext();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

        var ledger = new DetectionLedger("request-1");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Pipeline",
            Category = "Runtime",
            ConfidenceDelta = 0.2,
            Weight = 2,
            Reason = "processor timeout"
        });

        var evidence = CreateEvidence(
            riskBand: RiskBand.High,
            policyAction: DetectionPolicyAction.Challenge,
            ledger: ledger,
            signals: new Dictionary<string, object>
            {
                [SignalKeys.PrimarySignature] = "primary-hmac",
                ["pipeline.error.message"] = "processor timeout"
            });

        await dispatcher.DispatchAsync(context, evidence);

        var record = Assert.Single(sink.Records);
        Assert.Equal("stylobot.audit.error", record.Type);
        Assert.Equal("ErrorSignalAuditProcessor", record.SourceProcessor);
        Assert.Equal("Error", record.Severity);
        Assert.Equal("primary-hmac", record.PrimarySignature);
        Assert.Equal("/checkout", record.Path);
        Assert.Equal("POST", record.Method);
        Assert.Equal("Challenge", record.Action);
        Assert.Equal("High", record.RiskBand);
        Assert.Equal(0.8, record.BotProbability);
        Assert.Equal(0.9, record.Confidence);
        Assert.NotNull(record.Signals);
        Assert.True(record.Signals.ContainsKey("pipeline.error.message"));
        Assert.NotNull(record.DetectorDeltas);
        Assert.Equal(0.4, record.DetectorDeltas["Pipeline"]);
        Assert.Contains("processor timeout", record.Reasons!);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, record.Properties!["statusCode"]);
    }

    [Fact]
    public async Task AuditRecordWriter_WhenSinkThrows_ContinuesToRemainingSinks()
    {
        var capture = new CaptureSink();
        var writer = new AuditRecordWriter(
            [new ThrowingSink(), capture],
            NullLogger<AuditRecordWriter>.Instance);
        var record = new AuditRecord
        {
            Type = "test",
            SourceProcessor = "test",
            Timestamp = DateTime.UtcNow,
            RequestId = "request-1"
        };

        await writer.WriteAsync(record);

        Assert.Same(record, Assert.Single(capture.Records));
    }

    private static AuditProcessorDispatcher CreateDispatcher(
        IEnumerable<IAuditProcessor> processors,
        IAuditSink sink,
        AuditProcessorOptions options)
    {
        return new AuditProcessorDispatcher(
            processors,
            new AuditRecordWriter([sink], NullLogger<AuditRecordWriter>.Instance),
            Options.Create(options),
            NullLogger<AuditProcessorDispatcher>.Instance);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "request-1"
        };
        context.Request.Path = "/checkout";
        context.Request.Method = "POST";
        context.Response.StatusCode = StatusCodes.Status200OK;
        return context;
    }

    private static AggregatedEvidence CreateEvidence(
        RiskBand riskBand = RiskBand.Low,
        DetectionPolicyAction? policyAction = null,
        DetectionLedger? ledger = null,
        IReadOnlyDictionary<string, object>? signals = null)
    {
        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = 0.8,
            Confidence = 0.9,
            RiskBand = riskBand,
            PolicyName = "default",
            PolicyAction = policyAction,
            Signals = signals ?? new Dictionary<string, object>(),
            CategoryBreakdown = new Dictionary<string, CategoryScore>(),
            ContributingDetectors = new HashSet<string>()
        };
    }

    private sealed class SnapshotProcessor : IAuditProcessor
    {
        public int InvocationCount { get; private set; }
        public string Name => "snapshot";

        public async ValueTask ProcessAsync(
            AuditProcessingContext context,
            IAuditRecordWriter writer,
            CancellationToken ct = default)
        {
            InvocationCount++;
            await writer.WriteAsync(new AuditRecord
            {
                Type = "snapshot",
                SourceProcessor = Name,
                Timestamp = context.Metadata.Timestamp,
                RequestId = context.Metadata.RequestId,
                PrimarySignature = context.Metadata.PrimarySignature,
                Signals = context.Signals
            }, ct);
        }
    }

    private sealed class CaptureSink : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];
        public string Name => "capture";

        public ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IAuditSink
    {
        public string Name => "throwing";

        public ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default)
            => throw new InvalidOperationException("sink failed");
    }
}
