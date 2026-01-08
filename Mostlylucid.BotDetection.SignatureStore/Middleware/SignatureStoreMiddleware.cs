using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Yarp;
using Mostlylucid.BotDetection.SignatureStore.Hubs;
using Mostlylucid.BotDetection.SignatureStore.Models;
using Mostlylucid.BotDetection.SignatureStore.Repositories;
using System.Text.Json;

namespace Mostlylucid.BotDetection.SignatureStore.Middleware;

/// <summary>
/// Middleware that captures bot detection signatures and stores them in Postgres.
/// Works for both ASP.NET middleware and YARP gateway.
/// Non-blocking - signature storage happens asynchronously via fire-and-forget.
/// </summary>
public class SignatureStoreMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SignatureStoreMiddleware> _logger;

    public SignatureStoreMiddleware(
        RequestDelegate next,
        ILogger<SignatureStoreMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ISignatureRepository repository,
        ISignatureBroadcaster broadcaster,
        SignatureStoreOptions options)
    {
        // Continue pipeline
        await _next(context);

        // After response, capture signature if available (fire-and-forget)
        if (options.Enabled)
        {
            _ = CaptureAndStoreSignatureAsync(context, repository, broadcaster, options);
        }
    }

    private async Task CaptureAndStoreSignatureAsync(
        HttpContext context,
        ISignatureRepository repository,
        ISignatureBroadcaster broadcaster,
        SignatureStoreOptions options)
    {
        try
        {
            // Get signature from HttpContext items (set by YARP bot detection)
            var signature = context.Items["BotDetection.Signature"] as YarpBotSignature;

            if (signature == null)
            {
                _logger.LogDebug("No bot detection signature found in HttpContext");
                return;
            }

            // Convert to entity
            var entity = ConvertToEntity(signature, context, options);

            // Store in database (async, non-blocking from request path)
            await repository.StoreSignatureAsync(entity);

            // Broadcast to SignalR clients
            var queryResult = new SignatureQueryResult
            {
                SignatureId = entity.SignatureId,
                Timestamp = entity.Timestamp,
                BotProbability = entity.BotProbability,
                Confidence = entity.Confidence,
                RiskBand = entity.RiskBand,
                RequestPath = entity.RequestPath,
                RemoteIp = null,  // Never expose raw PII
                UserAgent = null, // Never expose raw PII
                BotName = entity.BotName,
                DetectorCount = entity.DetectorCount,
                SignatureJson = entity.SignatureJson
            };

            await broadcaster.BroadcastSignatureAsync(queryResult);

            _logger.LogDebug(
                "Stored and broadcasted signature {SignatureId} with BotProbability={BotProb:F2}",
                entity.SignatureId, entity.BotProbability);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture and store signature");
            // Don't throw - this is fire-and-forget, shouldn't affect request
        }
    }

    private SignatureEntity ConvertToEntity(
        YarpBotSignature signature,
        HttpContext context,
        SignatureStoreOptions options)
    {
        // Serialize full signature to JSON
        var signatureJson = JsonSerializer.Serialize(signature, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Extract signals for indexing (flatten signals dictionary)
        string? signalsJson = null;
        if (signature.Signals != null && signature.Signals.Count > 0)
        {
            signalsJson = JsonSerializer.Serialize(signature.Signals, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        // Calculate expiration if TTL is set
        DateTime? expiresAt = null;
        if (options.RetentionDays > 0)
        {
            expiresAt = DateTime.UtcNow.AddDays(options.RetentionDays);
        }

        return new SignatureEntity
        {
            SignatureId = signature.SignatureId,
            Timestamp = signature.Timestamp,
            BotProbability = signature.Detection.Confidence,
            Confidence = signature.Detection.Confidence,
            RiskBand = signature.Detection.IsMalicious ? "VeryHigh" : (signature.Detection.IsBot ? "Medium" : "Low"),
            RequestPath = signature.Path,
            RequestMethod = signature.Method,
            // DO NOT STORE RAW PII - these fields are obsolete
            RemoteIp = null,  // CRITICAL: Never store raw IP
            UserAgent = null, // CRITICAL: Never store raw UA
            BotName = signature.Detection.BotName,
            PolicyName = signature.Detection.Policy,
            DetectorCount = signature.DetectorOutputs?.Count ?? 0,
            ProcessingTimeMs = signature.ResponseTimeMs,
            SignatureJson = signatureJson,
            SignalsJson = signalsJson,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        };
    }
}
