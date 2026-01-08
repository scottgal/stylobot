using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
///     Extension methods for mapping the browser fingerprint endpoint.
/// </summary>
public static class BrowserFingerprintEndpointExtensions
{
    /// <summary>
    ///     Maps the browser fingerprint collection endpoint.
    ///     This endpoint receives fingerprint data from the client-side script.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The endpoint path. Default: "/bot-detection/fingerprint"</param>
    /// <returns>The route handler builder for further configuration.</returns>
    public static IEndpointConventionBuilder MapBotDetectionFingerprintEndpoint(
        this IEndpointRouteBuilder endpoints,
        string path = "/bot-detection/fingerprint")
    {
        return endpoints.MapPost(path, HandleFingerprintAsync)
            .WithName("BotDetectionFingerprint")
            .WithDisplayName("Bot Detection Browser Fingerprint")
            .AllowAnonymous(); // Must be accessible to all users
    }

    private static async Task<IResult> HandleFingerprintAsync(
        HttpContext context,
        IOptions<BotDetectionOptions> options,
        IBrowserTokenService tokenService,
        IBrowserFingerprintAnalyzer analyzer,
        IBrowserFingerprintStore store,
        BotDetectionMetrics? metrics = null,
        ILogger<BrowserFingerprintEndpoint>? logger = null)
    {
        var opts = options.Value;

        if (!opts.ClientSide.Enabled) return Results.NotFound();

        // Validate token from header
        var token = context.Request.Headers["X-ML-BotD-Token"].FirstOrDefault();
        var payload = tokenService.ValidateToken(context, token ?? "");

        if (payload == null)
        {
            logger?.LogDebug("Invalid or missing fingerprint token");
            metrics?.RecordError("ClientSide", "InvalidToken");
            return Results.BadRequest(new { error = "Invalid token" });
        }

        // Parse fingerprint data
        BrowserFingerprintData? data;
        try
        {
            data = await JsonSerializer.DeserializeAsync<BrowserFingerprintData>(
                context.Request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null) return Results.BadRequest(new { error = "Invalid data" });
        }
        catch (JsonException ex)
        {
            logger?.LogDebug(ex, "Failed to parse fingerprint data");
            return Results.BadRequest(new { error = "Invalid JSON" });
        }

        // Analyze fingerprint
        var result = analyzer.Analyze(data, payload.RequestId);

        // Store for correlation with subsequent requests
        store.Store(payload.IpHash, result);

        // Record metrics
        metrics?.RecordClientSideFingerprint(
            result.IsHeadless,
            result.BrowserIntegrityScore,
            result.DetectedAutomation);

        logger?.LogDebug(
            "Fingerprint received: RequestId={RequestId}, Headless={Headless}, Integrity={Integrity}",
            payload.RequestId, result.IsHeadless, result.BrowserIntegrityScore);

        // Return minimal response (client doesn't need full results)
        return Results.Ok(new
        {
            received = true,
            id = payload.RequestId
        });
    }
}

/// <summary>
///     Marker class for logging in the fingerprint endpoint.
/// </summary>
public class BrowserFingerprintEndpoint
{
}