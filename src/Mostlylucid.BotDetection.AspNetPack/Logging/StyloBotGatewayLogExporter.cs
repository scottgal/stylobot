using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.AspNetPack.Configuration;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using OtlpLogRecord = OpenTelemetry.Proto.Logs.V1.LogRecord;
using OtlpScopeLogs = OpenTelemetry.Proto.Logs.V1.ScopeLogs;
using OtlpSeverityNumber = OpenTelemetry.Proto.Logs.V1.SeverityNumber;

namespace Mostlylucid.BotDetection.AspNetPack.Logging;

public enum ExportOutcome { Success, NonSuccess, GatewayUnreachable }

public sealed record ExportResult(ExportOutcome Outcome)
{
    public bool Ok => Outcome == ExportOutcome.Success;
}

/// <summary>
///     Ships a batch of <see cref="LogRecord"/> entries to an OTLP/HTTP+protobuf logs endpoint.
///     Uses <c>Content-Type: application/x-protobuf</c> so the commercial OtelMesh receiver
///     (which requires protobuf — no content negotiation) accepts the payload.
/// </summary>
public sealed class StyloBotGatewayLogExporter
{
    private readonly HttpClient _http;
    private readonly IOptions<LogSinkOptions> _opts;
    private readonly ILogger<StyloBotGatewayLogExporter> _log;

    public StyloBotGatewayLogExporter(
        HttpClient http,
        IOptions<LogSinkOptions> opts,
        ILogger<StyloBotGatewayLogExporter> log)
    {
        _http = http;
        _opts = opts;
        _log = log;
    }

    public async Task<ExportResult> ExportAsync(IReadOnlyList<LogRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return new ExportResult(ExportOutcome.Success);

        try
        {
            var url = _opts.Value.GatewayEndpoint.TrimEnd('/') + "/v1/logs";
            var body = ToProtobuf(batch);
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
            using var resp = await _http.PostAsync(url, content, ct);
            return new ExportResult(resp.IsSuccessStatusCode ? ExportOutcome.Success : ExportOutcome.NonSuccess);
        }
        catch (HttpRequestException ex)
        {
            _log.LogDebug(ex, "Gateway log export: gateway unreachable");
            return new ExportResult(ExportOutcome.GatewayUnreachable);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Gateway log export failed");
            return new ExportResult(ExportOutcome.NonSuccess);
        }
    }

    /// <summary>
    ///     Sentinel bucket for records that carry no fingerprint id. Standard
    ///     <see cref="Dictionary{TKey,TValue}"/> throws on null keys, so we
    ///     bucket the null-fp records under this string and only emit the
    ///     <c>sb-fp</c> resource attribute when the bucket is a real fp.
    /// </summary>
    private const string OrphanBucket = "__sb_fp_orphan__";

    private static byte[] ToProtobuf(IReadOnlyList<LogRecord> batch)
    {
        // Group by fingerprint so each fingerprint gets its own ResourceLogs entry
        // with sb-fp in Resource.Attributes (the correlation key the OtelMesh uses).
        var byFp = new Dictionary<string, List<LogRecord>>(StringComparer.Ordinal);
        foreach (var r in batch)
        {
            var key = r.FingerprintId ?? OrphanBucket;
            if (!byFp.TryGetValue(key, out var list))
            {
                list = new List<LogRecord>();
                byFp[key] = list;
            }
            list.Add(r);
        }

        var request = new ExportLogsServiceRequest();

        foreach (var (fp, records) in byFp)
        {
            var resource = new Resource();
            if (fp != OrphanBucket)
            {
                resource.Attributes.Add(new KeyValue
                {
                    Key = "sb-fp",
                    Value = new AnyValue { StringValue = fp }
                });
            }

            var scopeLogs = new OtlpScopeLogs();
            foreach (var r in records)
            {
                var lr = new OtlpLogRecord
                {
                    TimeUnixNano = (ulong)((r.TimestampUtc.ToUniversalTime() - DateTime.UnixEpoch).Ticks * 100L),
                    SeverityNumber = MapSeverityNumber(r.Level),
                    SeverityText = r.Level.ToString(),
                    Body = new AnyValue { StringValue = r.Body }
                };
                lr.Attributes.Add(new KeyValue
                {
                    Key = "category",
                    Value = new AnyValue { StringValue = r.Category }
                });
                scopeLogs.LogRecords.Add(lr);
            }

            request.ResourceLogs.Add(new ResourceLogs
            {
                Resource = resource,
                ScopeLogs = { scopeLogs }
            });
        }

        return request.ToByteArray();
    }

    private static OtlpSeverityNumber MapSeverityNumber(LogLevel l) => l switch
    {
        LogLevel.Trace       => OtlpSeverityNumber.Trace,
        LogLevel.Debug       => OtlpSeverityNumber.Debug,
        LogLevel.Information => OtlpSeverityNumber.Info,
        LogLevel.Warning     => OtlpSeverityNumber.Warn,
        LogLevel.Error       => OtlpSeverityNumber.Error,
        LogLevel.Critical    => OtlpSeverityNumber.Fatal,
        _ => OtlpSeverityNumber.Info
    };
}
