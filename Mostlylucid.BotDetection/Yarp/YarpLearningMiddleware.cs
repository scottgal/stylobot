using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Yarp;

/// <summary>
///     Middleware for YARP learning mode - captures comprehensive bot signatures.
/// </summary>
public class YarpLearningMiddleware
{
    private readonly ILogger<YarpLearningMiddleware> _logger;
    private readonly RequestDelegate _next;
    private readonly YarpLearningModeOptions _options;
    private readonly Random _random = new();
    private readonly IYarpSignatureWriter _signatureWriter;

    public YarpLearningMiddleware(
        RequestDelegate next,
        ILogger<YarpLearningMiddleware> logger,
        IOptions<YarpLearningModeOptions> options,
        IYarpSignatureWriter signatureWriter)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
        _signatureWriter = signatureWriter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        // Check if path should be excluded
        if (ShouldExcludePath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // Check sampling rate
        if (!ShouldSample())
        {
            await _next(context);
            return;
        }

        // Capture signature
        var stopwatch = Stopwatch.StartNew();
        var initialStatusCode = 200;

        try
        {
            await _next(context);
            initialStatusCode = context.Response.StatusCode;
        }
        finally
        {
            stopwatch.Stop();

            // Only capture if confidence threshold met
            var confidence = context.GetBotConfidence();
            if (confidence >= _options.MinConfidenceToLog)
                await CaptureSignatureAsync(context, stopwatch.Elapsed, initialStatusCode);
        }
    }

    private bool ShouldExcludePath(PathString path)
    {
        foreach (var excludePath in _options.ExcludePaths)
            if (path.StartsWithSegments(excludePath, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private bool ShouldSample()
    {
        if (_options.SamplingRate >= 1.0)
            return true;

        if (_options.SamplingRate <= 0.0)
            return false;

        return _random.NextDouble() < _options.SamplingRate;
    }

    private async Task CaptureSignatureAsync(HttpContext context, TimeSpan elapsed, int statusCode)
    {
        try
        {
            var signature = new YarpBotSignature
            {
                SignatureId = $"sig_{Guid.NewGuid():N}",
                Timestamp = DateTime.UtcNow,
                Path = context.Request.Path,
                Method = context.Request.Method,
                ClientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                UserAgent = context.Request.Headers.UserAgent.ToString(),
                ResponseTimeMs = (int)elapsed.TotalMilliseconds,
                StatusCode = statusCode
            };

            // Capture detection results
            signature.Detection = new YarpDetectionResult
            {
                IsBot = context.IsBot(),
                Confidence = context.GetBotConfidence(),
                BotType = context.GetBotType()?.ToString(),
                BotName = context.GetBotName(),
                Category = context.GetBotCategory(),
                IsSearchEngine = context.IsSearchEngineBot(),
                IsMalicious = context.IsMaliciousBot(),
                IsSocialBot = context.IsSocialMediaBot(),
                Policy = context.Items["BotDetection.Policy"]?.ToString() ?? "unknown",
                Action = DetermineAction(context)
            };

            // Add detection reasons
            var reasons = context.GetDetectionReasons();
            foreach (var reason in reasons) signature.Detection.Reasons.Add($"{reason.Category}: {reason.Detail}");

            // Capture detector outputs if enabled
            if (_options.IncludeDetectorOutputs) CaptureDetectorOutputs(context, signature);

            // Capture blackboard signals if enabled
            if (_options.IncludeBlackboardSignals) CaptureBlackboardSignals(context, signature);

            // Capture full HTTP context if enabled (WARNING: May contain PII)
            if (_options.IncludeFullHttpContext) CaptureHttpContext(context, signature);

            // Capture request body if enabled (WARNING: May contain PII)
            if (_options.IncludeRequestBody && context.Request.ContentLength > 0)
                await CaptureRequestBodyAsync(context, signature);

            // Capture YARP routing info if available
            CaptureYarpRouting(context, signature);

            // Write signature
            await _signatureWriter.WriteAsync(signature);

            // Log to console if enabled
            if (_options.LogToConsole) LogSignatureToConsole(signature);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture YARP signature for {Path}", context.Request.Path);
        }
    }

    private void CaptureDetectorOutputs(HttpContext context, YarpBotSignature signature)
    {
        // Try to get detector contributions from context
        if (context.Items.TryGetValue("BotDetection.Contributions", out var contributionsObj) &&
            contributionsObj is List<DetectorContribution> contributions)
            foreach (var contrib in contributions)
                signature.DetectorOutputs[contrib.DetectorName] = new DetectorOutput
                {
                    Detector = contrib.DetectorName,
                    Confidence = contrib.Confidence,
                    Weight = contrib.Weight,
                    Contribution = contrib.Contribution,
                    Reason = contrib.Reason,
                    SuggestedBotType = contrib.SuggestedBotType?.ToString(),
                    ExecutionTimeMs = contrib.ExecutionTimeMs,
                    Wave = contrib.Wave
                };
    }

    private void CaptureBlackboardSignals(HttpContext context, YarpBotSignature signature)
    {
        // Try to get blackboard signals from context
        if (context.Items.TryGetValue("BotDetection.Signals", out var signalsObj) &&
            signalsObj is Dictionary<string, object> signals)
            foreach (var (key, value) in signals)
                signature.Signals[key] = value;
    }

    private void CaptureHttpContext(HttpContext context, YarpBotSignature signature)
    {
        signature.Headers = context.Request.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToString());

        signature.Cookies = context.Request.Cookies
            .Select(c => $"{c.Key}={c.Value}")
            .ToList();
    }

    private async Task CaptureRequestBodyAsync(HttpContext context, YarpBotSignature signature)
    {
        try
        {
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                signature.RequestBody = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture request body");
        }
    }

    private void CaptureYarpRouting(HttpContext context, YarpBotSignature signature)
    {
        // Try to get YARP routing info from context
        if (context.Items.TryGetValue("Yarp.Cluster", out var cluster) && cluster != null)
            signature.Cluster = cluster.ToString();

        if (context.Items.TryGetValue("Yarp.Destination", out var destination) && destination != null)
            signature.Destination = destination.ToString();
    }

    private string DetermineAction(HttpContext context)
    {
        if (context.Items.TryGetValue("BotDetection.Action", out var action) && action != null)
            return action.ToString() ?? "unknown";

        // In learning mode, always "allow"
        return "allow";
    }

    private void LogSignatureToConsole(YarpBotSignature signature)
    {
        var botStatus = signature.Detection.IsBot ? "YES" : "NO";
        var color = signature.Detection.IsBot ? ConsoleColor.Yellow : ConsoleColor.Green;

        Console.ForegroundColor = color;
        Console.WriteLine(
            $"[YARP Learning] {signature.SignatureId} | {signature.Path} | Bot: {botStatus} ({signature.Detection.Confidence:F2}) | {signature.Detection.BotType ?? "Unknown"}");
        Console.ResetColor();

        if (signature.DetectorOutputs.Count > 0)
            foreach (var (name, output) in signature.DetectorOutputs.OrderByDescending(kv => kv.Value.Contribution))
                Console.WriteLine(
                    $"  [{name}] {output.Confidence:F2} * {output.Weight:F2} = {output.Contribution:F2} | {output.Reason}");

        if (signature.Signals.Count > 0)
        {
            Console.Write("  Signals: ");
            Console.WriteLine(string.Join(", ", signature.Signals.Keys));
        }

        Console.WriteLine(
            $"  Total: {signature.Detection.Confidence:F2} | Action: {signature.Detection.Action}");
        Console.WriteLine();
    }
}

/// <summary>
///     Detector contribution for YARP learning capture.
/// </summary>
public class DetectorContribution
{
    public string DetectorName { get; set; } = "";
    public double Confidence { get; set; }
    public double Weight { get; set; }
    public double Contribution { get; set; }
    public string? Reason { get; set; }
    public BotType? SuggestedBotType { get; set; }
    public double ExecutionTimeMs { get; set; }
    public int Wave { get; set; }
}