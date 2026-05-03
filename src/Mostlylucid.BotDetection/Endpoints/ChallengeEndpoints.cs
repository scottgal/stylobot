using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Attributes;

namespace Mostlylucid.BotDetection.Endpoints;

/// <summary>
///     Proof-of-work challenge verification endpoint.
///     Validates micro-puzzle solutions, records solve timing metadata for the detection
///     feedback loop, and issues signed challenge tokens.
/// </summary>
public static class ChallengeEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    ///     Maps the PoW challenge verification endpoint.
    /// </summary>
    public static IEndpointRouteBuilder MapChallengeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/bot-detection/challenge");

        // Full detection runs (UA, IP, header, reputation, etc.) but with a high block
        // threshold: the visitor was SENT here by a challenge action, so they're expected
        // to be in the 0.5-0.7 range. Only confirmed bots (0.95+) get blocked.
        group.MapPost("/verify", (Delegate)(Func<HttpContext, Task<IResult>>)HandleVerify)
            .WithMetadata(new BotPolicyAttribute("default") { BlockThreshold = 0.95 })
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> HandleVerify(HttpContext context)
    {
        var logger = context.RequestServices.GetService<ILogger<SqliteChallengeStore>>();
        var store = context.RequestServices.GetRequiredService<IChallengeStore>();

        ChallengeVerifyRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ChallengeVerifyRequest>(JsonOptions);
        }
        catch
        {
            return Results.BadRequest(new { error = "Invalid request body" });
        }

        if (request is null || string.IsNullOrEmpty(request.ChallengeId))
            return Results.BadRequest(new { error = "Missing challengeId" });

        // Single-use: consume the challenge
        var challenge = store.ValidateAndConsume(request.ChallengeId);
        if (challenge is null)
            return Results.Json(new { error = "Challenge not found, expired, or already used" }, statusCode: 404);

        // Verify each puzzle solution - match by SeedIndex, not array position
        if (request.Solutions is null || request.Solutions.Length != challenge.PuzzleCount)
            return Results.BadRequest(new { error = $"Expected {challenge.PuzzleCount} solutions" });

        // Sort by SeedIndex and validate each index matches a puzzle
        var sortedSolutions = request.Solutions.OrderBy(s => s.SeedIndex).ToArray();
        for (var i = 0; i < challenge.PuzzleCount; i++)
        {
            var solution = sortedSolutions[i];
            if (solution.SeedIndex != i)
                return Results.BadRequest(new { error = $"Missing or duplicate solution for puzzle {i}" });

            var puzzle = challenge.Puzzles[i];
            if (!VerifyPuzzleSolution(puzzle, solution.Nonce))
            {
                logger?.LogDebug("Puzzle {Index} verification failed for challenge {Id}", i, request.ChallengeId);
                return Results.Json(new { error = $"Puzzle {i} verification failed" }, statusCode: 403);
            }
        }

        // All puzzles verified -- record timing metadata for feedback loop
        var timings = request.Metadata?.PuzzleTimingsMs ?? [];
        var totalDuration = request.Metadata?.TotalTimeMs ?? 0;
        var workerCount = request.Metadata?.WorkerCount ?? 0;

        // Compute timing jitter (coefficient of variation of per-puzzle timings)
        var jitter = 0.0;
        if (timings.Length > 1)
        {
            var mean = timings.Average();
            if (mean > 0)
            {
                var stdDev = Math.Sqrt(timings.Select(t => Math.Pow(t - mean, 2)).Average());
                jitter = stdDev / mean;
            }
        }

        var verification = new ChallengeVerificationResult
        {
            Signature = challenge.Signature,
            TotalSolveDurationMs = totalDuration,
            ReportedWorkerCount = workerCount,
            PuzzleCount = challenge.PuzzleCount,
            PuzzleTimingsMs = timings,
            TimingJitter = jitter,
            VerifiedAt = DateTimeOffset.UtcNow
        };

        store.RecordVerification(verification);

        // Issue signed challenge token cookie using the auto-generated or configured secret
        var tokenOptions = new ChallengeActionOptions { TokenValidityMinutes = 30 };
        var token = ChallengeActionPolicy.GenerateChallengeToken(context, tokenOptions);

        context.Response.Cookies.Append(tokenOptions.TokenCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromMinutes(tokenOptions.TokenValidityMinutes)
        });

        logger?.LogInformation(
            "PoW challenge verified for {Signature}: {Duration:F0}ms, {Workers} workers, {Puzzles} puzzles",
            challenge.Signature, totalDuration, workerCount, challenge.PuzzleCount);

        var returnUrl = SanitizeReturnUrl(request.ReturnUrl);

        // Return JSON for programmatic clients, redirect info for browsers
        // Token is in the HttpOnly cookie - don't also expose in JSON body (XSS risk)
        return Results.Json(new
        {
            success = true,
            returnUrl
        });
    }

    /// <summary>
    ///     Validates returnUrl is a safe relative path. Rejects absolute URLs,
    ///     protocol-relative URLs, and scheme injections to prevent open redirect.
    /// </summary>
    private static string SanitizeReturnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/";

        // Must be a relative path
        if (url.Contains("://") || url.StartsWith("//") || url.StartsWith("\\"))
            return "/";

        // Must start with /
        if (!url.StartsWith('/'))
            return "/";

        return url;
    }

    /// <summary>
    ///     Verifies that SHA256(seed + nonce_bytes) has the required leading zero hex chars.
    /// </summary>
    private static bool VerifyPuzzleSolution(PuzzleSeed puzzle, long nonce)
    {
        var nonceBytes = Encoding.UTF8.GetBytes(nonce.ToString());
        var input = new byte[puzzle.Seed.Length + nonceBytes.Length];
        puzzle.Seed.CopyTo(input, 0);
        nonceBytes.CopyTo(input, puzzle.Seed.Length);

        var hash = SHA256.HashData(input);
        var hex = Convert.ToHexString(hash);

        for (var i = 0; i < puzzle.RequiredZeros; i++)
        {
            if (i >= hex.Length || hex[i] != '0')
                return false;
        }

        return true;
    }
}

// Request DTOs

internal sealed record ChallengeVerifyRequest
{
    public string? ChallengeId { get; init; }
    public PuzzleSolution[]? Solutions { get; init; }
    public SolveMetadata? Metadata { get; init; }
    public string? ReturnUrl { get; init; }
}

internal sealed record PuzzleSolution
{
    public int SeedIndex { get; init; }
    public long Nonce { get; init; }
}

internal sealed record SolveMetadata
{
    public int WorkerCount { get; init; }
    public double TotalTimeMs { get; init; }
    public double[] PuzzleTimingsMs { get; init; } = [];
}
