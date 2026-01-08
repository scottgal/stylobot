using System.Threading.Channels;
using Mostlylucid.BotDetection.Console.Helpers;
using Mostlylucid.BotDetection.Console.Models;
using Mostlylucid.BotDetection.Models;
using Serilog;

namespace Mostlylucid.BotDetection.Console.Logging;

/// <summary>
///     Handles bot signature logging in JSON-LD format with async file writing
/// </summary>
public class SignatureLogger
{
    private readonly CancellationTokenSource _cts;
    private readonly Channel<SignatureEntry> _signatureQueue;
    private readonly Task _writerTask;

    public SignatureLogger()
    {
        // Create bounded channel with capacity 1000 (prevents memory issues if disk is slow)
        _signatureQueue = Channel.CreateBounded<SignatureEntry>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(() => BackgroundWriterAsync(_cts.Token));
    }

    /// <summary>
    ///     Log bot signature in JSON-LD format (schema.org SecurityAction) with ZERO-PII multi-factor signatures
    /// </summary>
    public void LogSignatureJsonLd(
        HttpContext context,
        BotDetectionResult detection,
        SignatureLoggingConfig config)
    {
        // Compute privacy-safe multi-factor signature hashes
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var referer = context.Request.Headers.Referer.ToString();
        var xForwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();

        var multiFactorSig = HmacHelper.ComputeMultiFactorSignature(
            config.SignatureHashKey,
            userAgent,
            clientIp,
            context.Request.Path.ToString(),
            referer);

        // Write to date-based JSONL file (manual JSON for AOT compatibility)
        try
        {
            var dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var filename = $"signatures-{dateStr}.jsonl";

            // Build JSON manually for AOT compatibility
            var reasonsJson = detection.Reasons != null
                ? string.Join(",", detection.Reasons.Select(r =>
                    JsonBuilder.BuildJsonObject(new Dictionary<string, object>
                    {
                        ["category"] = r.Category,
                        ["detail"] = r.Detail,
                        ["impact"] = r.ConfidenceImpact
                    }, config.PrettyPrintJsonLd ? 8 : 0)))
                : "";

            var indent = config.PrettyPrintJsonLd;
            var json = JsonBuilder.BuildJsonObject(new Dictionary<string, object>
            {
                ["@context"] = "https://schema.org",
                ["@type"] = "SecurityAction",
                ["agent"] = new Dictionary<string, object>
                {
                    ["@type"] = "SoftwareApplication",
                    ["name"] = "Mostlylucid.BotDetection.Console",
                    ["version"] = "1.0.0"
                },
                ["actionStatus"] = "CompletedActionStatus",
                ["result"] = new Dictionary<string, object>
                {
                    ["@type"] = "ThreatDetection",
                    ["detectedAt"] = DateTime.UtcNow.ToString("O"),
                    ["threatType"] = detection.BotType?.ToString() ?? "Unknown",
                    ["threatName"] = detection.BotName ?? "Unidentified",
                    ["confidenceScore"] = detection.ConfidenceScore,
                    ["riskLevel"] = detection.ConfidenceScore switch
                    {
                        >= 0.9 => "VeryHigh",
                        >= 0.7 => "High",
                        >= 0.5 => "Medium",
                        >= 0.3 => "Low",
                        _ => "VeryLow"
                    },
                    ["multiFactorSignature"] = new Dictionary<string, object>
                    {
                        ["primary"] = multiFactorSig.Primary,
                        ["ip"] = multiFactorSig.IpHash,
                        ["ua"] = multiFactorSig.UaHash,
                        ["path"] = multiFactorSig.PathHash,
                        ["referer"] = multiFactorSig.RefererHash
                    },
                    ["requestContext"] = new Dictionary<string, object>
                    {
                        ["path"] = context.Request.Path.ToString(),
                        ["method"] = context.Request.Method,
                        ["protocol"] = context.Request.Protocol,
                        ["hasReferer"] = !string.IsNullOrEmpty(referer),
                        ["hasXForwardedFor"] = !string.IsNullOrEmpty(xForwardedFor)
                    },
                    ["reasons"] = $"[{reasonsJson}]"
                }
            }, indent ? 0 : 0);

            // Log to console (structured)
            Log.Information(
                "[JSON-LD-SIGNATURE] Primary signature: {PrimarySignature}, Confidence: {Confidence:F2}, Type: {ThreatType}",
                multiFactorSig.Primary,
                detection.ConfidenceScore,
                detection.BotType?.ToString() ?? "Unknown");

            // Queue for async writing (non-blocking)
            var entry = new SignatureEntry(filename, json);
            if (!_signatureQueue.Writer.TryWrite(entry))
            {
                Log.Warning("Signature queue full - waiting to write signature");
                // Fall back to synchronous write if queue is full
                File.AppendAllText(filename, json + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write signature to file");
        }
    }

    /// <summary>
    ///     Background task that writes signatures to disk asynchronously
    /// </summary>
    private async Task BackgroundWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in _signatureQueue.Reader.ReadAllAsync(cancellationToken))
                try
                {
                    await File.AppendAllTextAsync(entry.Filename, entry.JsonContent + Environment.NewLine,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to write signature to {Filename}", entry.Filename);
                }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    ///     Flush and stop the background writer (call on shutdown)
    /// </summary>
    public async Task FlushAndStopAsync()
    {
        _signatureQueue.Writer.Complete();
        await _writerTask;
        _cts.Cancel();
    }

    private record SignatureEntry(string Filename, string JsonContent);
}