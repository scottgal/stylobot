using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.AspNetPack.Logging;

public enum ExportOutcome { Success, NonSuccess, GatewayUnreachable }

public sealed record ExportResult(ExportOutcome Outcome)
{
    public bool Ok => Outcome == ExportOutcome.Success;
}

/// <summary>
///     Ships a batch of <see cref="LogRecord"/> entries to an OTLP/HTTP+JSON logs endpoint.
///     Uses the JSON encoding of the OTLP log export protocol (Content-Type:
///     application/json) so the FOSS pack carries no protobuf dependency.
/// </summary>
public sealed class StyloBotGatewayLogExporter
{
    private readonly HttpClient _http;
    private readonly string _gatewayEndpoint;
    private readonly ILogger<StyloBotGatewayLogExporter> _log;

    public StyloBotGatewayLogExporter(
        HttpClient http,
        string gatewayEndpoint,
        ILogger<StyloBotGatewayLogExporter> log)
    {
        _http = http;
        _gatewayEndpoint = gatewayEndpoint;
        _log = log;
    }

    public async Task<ExportResult> ExportAsync(IReadOnlyList<LogRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return new ExportResult(ExportOutcome.Success);

        try
        {
            var url = _gatewayEndpoint.TrimEnd('/') + "/v1/logs";
            var body = ToOtlpJson(batch);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
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

    private static string ToOtlpJson(IReadOnlyList<LogRecord> batch)
    {
        var logRecords = new List<object>(batch.Count);
        foreach (var r in batch)
        {
            var attrs = new List<object>
            {
                new { key = "category", value = new { stringValue = r.Category } }
            };
            if (r.FingerprintId is not null)
                attrs.Add(new { key = "sb-fp", value = new { stringValue = r.FingerprintId } });

            var lr = new Dictionary<string, object>
            {
                ["timeUnixNano"] = ((ulong)r.TimestampUtc.Ticks * 100UL).ToString(),
                ["severityNumber"] = MapSeverityNumber(r.Level),
                ["severityText"] = r.Level.ToString(),
                ["body"] = new { stringValue = r.Body },
                ["attributes"] = attrs
            };
            if (r.TraceIdHex is { Length: 32 }) lr["traceId"] = r.TraceIdHex;
            if (r.SpanIdHex  is { Length: 16 }) lr["spanId"]  = r.SpanIdHex;

            logRecords.Add(lr);
        }

        var payload = new
        {
            resourceLogs = new[]
            {
                new
                {
                    resource = new { attributes = Array.Empty<object>() },
                    scopeLogs = new[]
                    {
                        new { logRecords }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static int MapSeverityNumber(LogLevel l) => l switch
    {
        LogLevel.Trace       => 1,
        LogLevel.Debug       => 5,
        LogLevel.Information => 9,
        LogLevel.Warning     => 13,
        LogLevel.Error       => 17,
        LogLevel.Critical    => 21,
        _ => 9
    };
}
