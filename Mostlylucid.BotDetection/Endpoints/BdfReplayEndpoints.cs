using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Endpoints;

/// <summary>
///     BDF (Bot Detection Format) replay endpoint.
///     Accepts BDF v2 files and runs each request through the real detection pipeline,
///     comparing actual results to expected detection for regression testing.
/// </summary>
public static class BdfReplayEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Rate limiter
    private static readonly ConcurrentDictionary<string, List<DateTime>> RateLimitWindow = new();

    /// <summary>
    ///     Detectors that produce degraded/different results when replaying from synthetic context
    ///     (loopback IP, no real TLS, no TCP fingerprint, no HTTP/2 frame data).
    /// </summary>
    private static readonly List<string> DegradedDetectors =
    [
        "IpContributor",
        "TlsFingerprintContributor",
        "TcpIpFingerprintContributor",
        "Http2FingerprintContributor",
        "Http3FingerprintContributor",
        "BehavioralWaveformContributor",
        "ResponseBehaviorContributor",
        "FastPathReputationContributor",
        "ReputationBiasContributor"
    ];

    /// <summary>
    ///     Maps BDF replay endpoints to the specified route prefix.
    ///     Follows the same pattern as <see cref="TrainingDataEndpoints.MapBotTrainingEndpoints"/>.
    /// </summary>
    public static RouteGroupBuilder MapBdfReplayEndpoints(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/bot-detection/bdf-replay")
    {
        var group = endpoints.MapGroup(prefix)
            .WithTags("BDF Replay")
            .AddEndpointFilter(async (context, next) =>
            {
                var options = context.HttpContext.RequestServices
                    .GetService(typeof(IOptions<BotDetectionOptions>)) as IOptions<BotDetectionOptions>;
                var config = options?.Value.BdfReplay ?? new BdfReplayOptions();

                // Gate: endpoints disabled (off by default)
                if (!config.Enabled)
                    return Results.NotFound();

                // Gate: API key required
                if (config.RequireApiKey)
                {
                    if (!context.HttpContext.Request.Headers.TryGetValue("X-BdfReplay-Api-Key", out var apiKey)
                        || !HasValidApiKey(apiKey.ToString(), config.ApiKeys))
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetService(typeof(ILogger<BotDetectionOptions>)) as ILogger;
                        logger?.LogWarning("BDF replay access denied: invalid or missing API key from {IP}",
                            context.HttpContext.Connection.RemoteIpAddress);
                        return Results.Json(new { error = "Valid X-BdfReplay-Api-Key header required" },
                            statusCode: StatusCodes.Status401Unauthorized);
                    }
                }

                // Gate: rate limiting
                if (config.RateLimitPerMinute > 0)
                {
                    var clientIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    if (!CheckRateLimit(clientIp, config.RateLimitPerMinute))
                    {
                        context.HttpContext.Response.Headers["Retry-After"] = "60";
                        return Results.Json(new { error = "Rate limit exceeded" },
                            statusCode: StatusCodes.Status429TooManyRequests);
                    }
                }

                return await next(context);
            });

        group.MapPost("/replay", ReplayBdf)
            .WithName("ReplayBdf")
            .WithSummary("Replay a BDF v2 document through the detection pipeline and compare results")
            .Accepts<BdfReplayRequest>("application/json")
            .Produces<BdfReplayResponse>()
            .Produces(StatusCodes.Status400BadRequest);

        return group;
    }

    private static async Task<IResult> ReplayBdf(
        HttpContext httpContext,
        IBotDetectionService detectionService)
    {
        var options = httpContext.RequestServices
            .GetService(typeof(IOptions<BotDetectionOptions>)) as IOptions<BotDetectionOptions>;
        var config = options?.Value.BdfReplay ?? new BdfReplayOptions();
        var logger = httpContext.RequestServices
            .GetService(typeof(ILogger<BotDetectionOptions>)) as ILogger;

        // Deserialize BDF from request body
        BdfReplayRequest? bdf;
        try
        {
            bdf = await JsonSerializer.DeserializeAsync<BdfReplayRequest>(
                httpContext.Request.Body, ReadOptions, httpContext.RequestAborted);
        }
        catch (JsonException ex)
        {
            return Results.Json(new { error = "Invalid BDF JSON", detail = ex.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (bdf?.Requests == null || bdf.Requests.Count == 0)
            return Results.Json(new { error = "BDF must contain at least one request" },
                statusCode: StatusCodes.Status400BadRequest);

        // Cap requests
        var maxRequests = config.MaxRequestsPerReplay;
        var requests = bdf.Requests.Count > maxRequests
            ? bdf.Requests.Take(maxRequests).ToList()
            : bdf.Requests;

        var results = new List<BdfReplayResult>();
        var falsePositives = 0;
        var falseNegatives = 0;
        var matches = 0;

        for (var i = 0; i < requests.Count; i++)
        {
            var req = requests[i];

            // Build synthetic HttpContext
            var syntheticContext = new DefaultHttpContext
            {
                RequestServices = httpContext.RequestServices
            };
            syntheticContext.Request.Method = req.Method ?? "GET";
            syntheticContext.Request.Path = req.Path ?? "/";
            syntheticContext.Request.Scheme = "https";
            syntheticContext.Request.Host = httpContext.Request.Host;
            syntheticContext.Connection.RemoteIpAddress = IPAddress.Loopback;

            // Apply headers from BDF
            if (req.Headers != null)
            {
                foreach (var (key, value) in req.Headers)
                    syntheticContext.Request.Headers[key] = value;
            }

            // Run detection
            BotDetectionResult result;
            try
            {
                result = await detectionService.DetectAsync(syntheticContext, httpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "BDF replay: detection failed for request {Index} ({Path})", i, req.Path);
                results.Add(new BdfReplayResult
                {
                    RequestIndex = i,
                    Path = req.Path ?? "/",
                    Error = "Detection failed: " + ex.Message
                });
                continue;
            }

            var actual = new BdfReplayActual
            {
                IsBot = result.IsBot,
                BotProbability = Math.Round(result.ConfidenceScore, 4),
                BotType = result.BotType?.ToString(),
                TopReasons = result.Reasons.Select(r => r.Detail).Take(5).ToList()
            };

            // Compare with expected
            var isMatch = true;
            if (req.ExpectedDetection != null)
            {
                if (req.ExpectedDetection.IsBot != result.IsBot)
                {
                    isMatch = false;
                    if (result.IsBot && !req.ExpectedDetection.IsBot)
                        falsePositives++;
                    else if (!result.IsBot && req.ExpectedDetection.IsBot)
                        falseNegatives++;
                }
            }
            if (isMatch) matches++;

            results.Add(new BdfReplayResult
            {
                RequestIndex = i,
                Path = req.Path ?? "/",
                Expected = req.ExpectedDetection != null
                    ? new BdfReplayExpected
                    {
                        IsBot = req.ExpectedDetection.IsBot,
                        BotProbability = req.ExpectedDetection.BotProbability,
                        RiskBand = req.ExpectedDetection.RiskBand
                    }
                    : null,
                Actual = actual,
                Match = isMatch
            });

            // Delay between requests (capped at 5s)
            if (req.DelayAfter > 0 && i < requests.Count - 1)
            {
                var delay = Math.Min(req.DelayAfter, 5.0);
                await Task.Delay(TimeSpan.FromSeconds(delay), httpContext.RequestAborted);
            }
        }

        var totalWithExpectations = results.Count(r => r.Expected != null);
        var response = new BdfReplayResponse
        {
            ScenarioName = bdf.ScenarioName ?? "unnamed",
            Results = results,
            Summary = new BdfReplaySummary
            {
                MatchRate = totalWithExpectations > 0
                    ? Math.Round((double)matches / totalWithExpectations, 4)
                    : 1.0,
                FalsePositives = falsePositives,
                FalseNegatives = falseNegatives,
                TotalRequests = results.Count,
                Truncated = bdf.Requests.Count > maxRequests
            },
            DegradedDetectors = DegradedDetectors
        };

        return Results.Json(response, JsonOptions);
    }

    #region Auth & Rate Limiting

    private static bool HasValidApiKey(string providedKey, IReadOnlyList<string> configuredKeys)
    {
        if (configuredKeys.Count == 0 || string.IsNullOrEmpty(providedKey))
            return false;

        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        var isMatch = false;

        foreach (var key in configuredKeys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            var keyBytes = Encoding.UTF8.GetBytes(key);
            isMatch |= keyBytes.Length == providedBytes.Length &&
                       CryptographicOperations.FixedTimeEquals(providedBytes, keyBytes);
        }

        return isMatch;
    }

    private static bool CheckRateLimit(string clientIp, int maxPerMinute)
    {
        var now = DateTime.UtcNow;
        var window = RateLimitWindow.GetOrAdd(clientIp, _ => new List<DateTime>());

        lock (window)
        {
            window.RemoveAll(t => (now - t).TotalMinutes > 1);
            if (window.Count >= maxPerMinute)
                return false;
            window.Add(now);
            return true;
        }
    }

    #endregion
}

#region Request/Response Models

/// <summary>
///     BDF replay request — subset of BDF v2 fields needed for replay.
/// </summary>
public sealed class BdfReplayRequest
{
    public string? ScenarioName { get; set; }
    public List<BdfReplayRequestItem> Requests { get; set; } = [];
}

public sealed class BdfReplayRequestItem
{
    public string? Method { get; set; }
    public string? Path { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public double DelayAfter { get; set; }
    public BdfReplayExpectedDetection? ExpectedDetection { get; set; }
}

public sealed class BdfReplayExpectedDetection
{
    public bool IsBot { get; set; }
    public double BotProbability { get; set; }
    public string? RiskBand { get; set; }
}

public sealed class BdfReplayResponse
{
    public required string ScenarioName { get; set; }
    public List<BdfReplayResult> Results { get; set; } = [];
    public required BdfReplaySummary Summary { get; set; }
    public List<string> DegradedDetectors { get; set; } = [];
}

public sealed class BdfReplayResult
{
    public int RequestIndex { get; set; }
    public required string Path { get; set; }
    public BdfReplayExpected? Expected { get; set; }
    public BdfReplayActual? Actual { get; set; }
    public bool Match { get; set; }
    public string? Error { get; set; }
}

public sealed class BdfReplayExpected
{
    public bool IsBot { get; set; }
    public double BotProbability { get; set; }
    public string? RiskBand { get; set; }
}

public sealed class BdfReplayActual
{
    public bool IsBot { get; set; }
    public double BotProbability { get; set; }
    public string? BotType { get; set; }
    public List<string> TopReasons { get; set; } = [];
}

public sealed class BdfReplaySummary
{
    public double MatchRate { get; set; }
    public int FalsePositives { get; set; }
    public int FalseNegatives { get; set; }
    public int TotalRequests { get; set; }
    public bool Truncated { get; set; }
}

#endregion
