using Mostlylucid.BotDetection.Demo.Hubs;
using Mostlylucid.BotDetection.Demo.Services;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Demo.Middleware;

/// <summary>
///     Middleware that captures bot detection signatures after they're generated
///     and stores them for demo display purposes.
///     Also broadcasts new signatures via SignalR.
/// </summary>
public class SignatureCaptureMiddleware
{
    private readonly SignatureBroadcaster _broadcaster;
    private readonly ILogger<SignatureCaptureMiddleware> _logger;
    private readonly RequestDelegate _next;
    private readonly SignatureStore _signatureStore;

    public SignatureCaptureMiddleware(
        RequestDelegate next,
        SignatureStore signatureStore,
        SignatureBroadcaster broadcaster,
        ILogger<SignatureCaptureMiddleware> logger)
    {
        _next = next;
        _signatureStore = signatureStore;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Generate signature ID BEFORE pipeline execution so it's available for YARP
        var signatureId = GenerateSignatureId(context);
        context.Items["BotDetection.SignatureId"] = signatureId;

        // Execute the rest of the pipeline
        await _next(context);

        // After bot detection has run, capture the signature
        try
        {
            // Check if bot detection ran and created evidence
            if (context.Items.TryGetValue("BotDetection.Evidence", out var evidenceObj) &&
                evidenceObj is AggregatedEvidence evidence)
            {
                // Store signature
                _signatureStore.StoreSignature(signatureId, evidence, context);

                // Add signature ID to response headers for client reference
                context.Response.Headers["X-Signature-ID"] = signatureId;

                // Broadcast to SignalR subscribers
                var stored = _signatureStore.GetSignature(signatureId);
                if (stored != null) await _broadcaster.BroadcastSignature(stored);

                _logger.LogTrace(
                    "Captured signature {SignatureId} - BotProb: {BotProb:F2}",
                    signatureId,
                    evidence.BotProbability);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture signature");
        }
    }

    private static string GenerateSignatureId(HttpContext context)
    {
        // Generate a unique ID based on request metadata
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var random = Guid.NewGuid().ToString("N")[..8];

        return $"{timestamp}-{random}";
    }
}

/// <summary>
///     Extension methods for registering signature capture middleware
/// </summary>
public static class SignatureCaptureMiddlewareExtensions
{
    public static IApplicationBuilder UseSignatureCapture(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SignatureCaptureMiddleware>();
    }
}