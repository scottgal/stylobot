using System.Security.Cryptography;
using System.Text;
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

        // Guard against oversized bodies: the fingerprint payload is ~2 KB, so a
        // large body is abuse. Cap before buffering into memory.
        const int maxBodyBytes = 64 * 1024;
        if (context.Request.ContentLength is > maxBodyBytes)
            return Results.BadRequest(new { error = "Payload too large" });

        // Buffer the raw body so we can BOTH HMAC-verify it and deserialize it.
        string bodyJson;
        try
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            bodyJson = await reader.ReadToEndAsync();
        }
        catch
        {
            return Results.BadRequest(new { error = "Unreadable body" });
        }

        if (bodyJson.Length > maxBodyBytes)
            return Results.BadRequest(new { error = "Payload too large" });

        // Token: header preferred (main fingerprint script sets the configured token
        // header, default X-SB-Client-Token); the body `t` field is the sendBeacon /
        // adblocker-probe fallback (no header support). data.BodyToken is read after
        // parse below. Header names are operator-configurable for white-labelling.
        var headerToken = context.Request.Headers[opts.ClientSide.TokenHeader].FirstOrDefault();
        var providedSig = context.Request.Headers[opts.ClientSide.SignatureHeader].FirstOrDefault();

        BrowserFingerprintData? data;
        try
        {
            data = JsonSerializer.Deserialize<BrowserFingerprintData>(
                bodyJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null) return Results.BadRequest(new { error = "Invalid data" });
        }
        catch (JsonException ex)
        {
            logger?.LogDebug(ex, "Failed to parse fingerprint data");
            return Results.BadRequest(new { error = "Invalid JSON" });
        }

        var token = headerToken ?? data.BodyToken;

        // Payload-to-token binding: HMAC-SHA256(key = token, msg = raw body). Proves
        // the beacon is a real, token-bound browser and not an off-browser replay of
        // a canned payload with a captured token. Verified when a signature is
        // present; required outright when RequirePayloadSignature is set (which then
        // rejects the header-less sendBeacon fallback -- fetch-only posture).
        if (!VerifyPayloadSignature(bodyJson, token, providedSig, opts.ClientSide, out var sigReason))
        {
            logger?.LogDebug("Fingerprint payload signature rejected: {Reason}", sigReason);
            metrics?.RecordError("ClientSide", "InvalidSignature");
            return Results.BadRequest(new { error = "Invalid payload signature" });
        }

        // Validate token: header preferred (existing fingerprint flow); body
        // field is the adblocker-probe fallback.
        var payload = tokenService.ValidateToken(context, token ?? "");

        if (payload == null)
        {
            logger?.LogDebug("Invalid or missing fingerprint token");
            metrics?.RecordError("ClientSide", "InvalidToken");
            return Results.BadRequest(new { error = "Invalid token" });
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

    /// <summary>
    ///     Verifies the beacon payload is HMAC-bound to the browser token:
    ///     base64(HMAC-SHA256(key = token, msg = raw body)). Returns true when the
    ///     signature verifies, or when no signature is present and
    ///     <see cref="ClientSideOptions.RequirePayloadSignature"/> is false (rollout /
    ///     sendBeacon fallback). The token is client-held, so this proves channel
    ///     provenance + payload integrity (no off-browser replay of a canned payload),
    ///     not value truthfulness -- the browser-characteristic consistency check does
    ///     that.
    /// </summary>
    internal static bool VerifyPayloadSignature(
        string body, string? token, string? providedSig, ClientSideOptions clientSide, out string reason)
    {
        if (string.IsNullOrEmpty(providedSig))
        {
            if (clientSide.RequirePayloadSignature)
            {
                reason = "signature required but absent";
                return false;
            }

            reason = "no signature (verification skipped)";
            return true;
        }

        if (string.IsNullOrEmpty(token))
        {
            reason = "signature present but no token to verify against";
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromBase64String(providedSig);
        }
        catch (FormatException)
        {
            reason = "signature not valid base64";
            return false;
        }

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(body));

        if (provided.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            reason = "verified";
            return true;
        }

        reason = "signature mismatch";
        return false;
    }
}

/// <summary>
///     Marker class for logging in the fingerprint endpoint.
/// </summary>
public class BrowserFingerprintEndpoint
{
}