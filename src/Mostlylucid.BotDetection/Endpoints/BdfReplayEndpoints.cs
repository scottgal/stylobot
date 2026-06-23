using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    ///     Wire-format options for the BDF replay endpoint. Internal so the integration
    ///     test deserializes responses with the same shape the endpoint emits, keeping
    ///     "what we accept" and "what tests parse" in lockstep.
    /// </summary>
    internal static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Rate limiter (bounded at 10K IPs)
    private static readonly ConcurrentDictionary<string, List<DateTime>> RateLimitWindow = new();
    private const int MaxRateLimitEntries = 10_000;

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
                    .GetRequiredService<IOptions<BotDetectionOptions>>();
                var config = options.Value.BdfReplay;

                // Gate: endpoints disabled (off by default)
                if (!config.Enabled)
                {
                    var logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("BdfReplay");
                    logger?.LogWarning("BDF replay disabled. BdfReplay.Enabled={Enabled}", config.Enabled);
                    return Results.NotFound();
                }

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
            .WithMetadata(new Attributes.BotPolicyAttribute("default") { BlockThreshold = 0.95 })
            .WithSummary("Replay a BDF v2 document through the detection pipeline and compare results")
            .Accepts<BdfReplayRequest>("application/json")
            .Produces<BdfReplayResponse>()
            .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/reset-identity", ResetIdentityStore)
            .WithName("ResetIdentityStore")
            .WithMetadata(new Attributes.BotPolicyAttribute("default") { BlockThreshold = 0.95 })
            .WithSummary("Truncate the identity store so BDF replay scenarios start from a clean state");

        return group;
    }

    /// <summary>
    ///     Truncates every identity table on the running fingerprint store so a BDF rig
    ///     can run each scenario against a deterministic clean state. Returns the count
    ///     of rows deleted from each table for the test rig's confirmation. Cheap because
    ///     identity tables are tiny in tests; only suitable for test/dev use.
    /// </summary>
    private static async Task<IResult> ResetIdentityStore(
        Identity.IFingerprintStore store,
        Identity.IdentityProcessingCoordinator coordinator,
        Identity.IdentityArchetypeRegistry archetypes,
        Orchestration.ContributingDetectors.FingerprintDimSnapshotCache snapshotCache,
        CancellationToken ct)
    {
        var counts = await store.TruncateAllAsync(ct);
        // Clear the coordinator's in-memory coalesce dict alongside the DB truncate.
        // Without this, a previous scenario's unresolved Pass 2 entries can shed a
        // fresh allocation in the next scenario that happens to reuse a fingerprint
        // id - producing the "1/N requests had no identity.fingerprint_id" flake.
        coordinator.ResetInflight();
        // Same reasoning for the per-fingerprint dim-snapshot cache used by
        // IdentityChangeContributor: after the DB truncate, fingerprint ids
        // get reallocated from 1; without flushing, scenario N inherits
        // scenario N-1's surface-dim baselines and trips spurious risk.* signals.
        snapshotCache.Reset();
        // Reload archetypes from embedded YAML so calibration-driven mutations
        // (variance multipliers, refined centroids, pin counters) from earlier
        // scenarios don't bleed into the next one. Without this, an earlier
        // scenario's umbrella shrinkage could leave (say) the curl-tool basin
        // wider than safari-mobile, causing fp-safari-ios to match curl-tool
        // in the next scenario.
        archetypes.ResetToSeedState();
        return Results.Ok(counts);
    }

    private static async Task<IResult> ReplayBdf(
        HttpContext httpContext,
        Orchestration.IDetectionOrchestrator orchestrator)
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

        // BDF replay's intent is to exercise the full detection pipeline on every request
        // — measuring detection accuracy, signal flow, and identity stability. The verdict
        // cache's Skip path bypasses the matcher entirely once a primary signature has a
        // confident cached verdict, which would (correctly, in production) mask the per-
        // request behaviour the rig is trying to assert on. Disable the cache for replay so
        // every request runs the full waveform.
        var replayPolicy = Policies.DetectionPolicy.Default with
        {
            SignatureCache = Policies.DetectionPolicy.Default.SignatureCache with { Enabled = false }
        };

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
            // Use a unique synthetic IP per scenario so reputation doesn't cascade.
            // Each scenario gets a unique /24 subnet from TEST-NET ranges (RFC 5737).
            // This prevents subnet-level reputation from bleeding between scenarios.
            // Deterministic hash (not string.GetHashCode which is randomized per process)
            var scenarioBytes = System.Text.Encoding.UTF8.GetBytes(bdf.ScenarioName ?? "default");
            var scenarioHash = (uint)System.IO.Hashing.XxHash32.HashToUInt32(scenarioBytes);
            var octet2 = (int)((scenarioHash >> 8) % 254) + 1;
            var octet3 = (int)(scenarioHash % 254) + 1;
            var syntheticIp = IPAddress.Parse($"192.0.{octet2}.{octet3}");
            syntheticContext.Connection.RemoteIpAddress = syntheticIp;

            // Mark the synthetic context as trusted for the TransportHeaderTrust
            // gate so contributor read paths (TlsFingerprintContributor's
            // X-JA3-Hash / X-JA4 / X-TLS-Cipher header reads, plus the
            // Http2/3/TcpIp peers) actually fire. Without this the synthetic
            // RFC 5737 TEST-NET IP reads as an untrusted public peer under the
            // gate's Auto fallthrough and every forwarded TLS / JA3 / JA4
            // header is skipped on every BDF replay -- the
            // TlsForwardingScenario_EdgeInjectedHeadersProduceTlsSignals test
            // asserted exactly this surface. The key is a constant defined on
            // TransportHeaderTrust; only the BDF replay codepath uses it, and
            // the replay endpoint itself is api-key + rate-limit gated upstream.
            syntheticContext.Items[Mostlylucid.BotDetection.Proxy.TransportHeaderTrust.SyntheticTrustOverrideKey] = true;

            // Apply headers from BDF (allowlist to prevent injection of internal control headers)
            if (req.Headers != null)
            {
                foreach (var (key, value) in req.Headers)
                {
                    // Block internal StyloBot headers and host manipulation
                    if (key.StartsWith("X-SB-", StringComparison.OrdinalIgnoreCase) ||
                        key.StartsWith("X-Bot-", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase))
                        continue;

                    syntheticContext.Request.Headers[key] = value;
                }
            }

            // Run detection through the active orchestrator (whichever IDetectionOrchestrator
            // implementation is registered in DI). Previously hardcoded to BlackboardOrchestrator,
            // which masked regressions in the Ephemeral path.
            Orchestration.AggregatedEvidence evidence;
            try
            {
                evidence = await orchestrator.DetectWithPolicyAsync(
                    syntheticContext, replayPolicy, httpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "BDF replay: detection failed for request {Index} ({Path})", i, req.Path);
                results.Add(new BdfReplayResult
                {
                    RequestIndex = i,
                    Path = req.Path ?? "/",
                    Error = "Detection failed (see server logs for details)"
                });
                continue;
            }

            // Probe the signal flow that downstream UI consumers depend on. False on any of
            // these means the dashboard degrades silently — see docs/architecture/signal-contracts.md.
            var signals = evidence.Signals;
            var signalProbes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [Models.SignalKeys.PrimarySignature] = signals.ContainsKey(Models.SignalKeys.PrimarySignature),
                [Models.SignalKeys.UserAgentBotName] = signals.ContainsKey(Models.SignalKeys.UserAgentBotName),
                [Models.SignalKeys.UserAgentBotType] = signals.ContainsKey(Models.SignalKeys.UserAgentBotType),
                [Models.SignalKeys.UserAgentFamily]  = signals.ContainsKey(Models.SignalKeys.UserAgentFamily),
                // TLS forwarding signals. Only land when the reverse proxy is forwarding
                // edge-computed TLS metadata (X-JA3-Hash / X-JA4 / X-Client-TLS-*). Probe
                // is False for the no-edge-forwarding case; True confirms the read path
                // in TlsFingerprintContributor + DetectionBroadcastMiddleware fired.
                [Models.SignalKeys.TlsIsHttps]    = signals.ContainsKey(Models.SignalKeys.TlsIsHttps),
                [Models.SignalKeys.TlsAvailable]  = signals.ContainsKey(Models.SignalKeys.TlsAvailable),
                ["tls.ja3_hash"]    = signals.ContainsKey("tls.ja3_hash"),
                ["tls.ja4"]         = signals.ContainsKey("tls.ja4"),
                ["tls.version"]     = signals.ContainsKey("tls.version"),
                ["tls.protocol"]    = signals.ContainsKey("tls.protocol"),
                // Foundation signal asserted by the synthesizer's archetype-name branch.
                // Only meaningful when Identity:Enabled = true; absent at every-request when off.
                [Models.SignalKeys.IdentityArchetypeName] = signals.ContainsKey(Models.SignalKeys.IdentityArchetypeName),
                // Threat-intel signals. EndpointRisk + EndpointRiskSensitive only land when
                // ThreatIntel:Enabled = true (the contributor short-circuits otherwise) - the
                // probe is False in that case, which is the right read for downstream tooling
                // that wants "does the request path get any risk modulation?".
                [Models.SignalKeys.EndpointRisk] = signals.ContainsKey(Models.SignalKeys.EndpointRisk),
                [Models.SignalKeys.EndpointRiskSensitive] = signals.ContainsKey(Models.SignalKeys.EndpointRiskSensitive),
                [Models.SignalKeys.ThreatIntelScore] = signals.ContainsKey(Models.SignalKeys.ThreatIntelScore),
                [Models.SignalKeys.IntelClasses] = signals.ContainsKey(Models.SignalKeys.IntelClasses),
                [Models.SignalKeys.IntelHardGate] = signals.ContainsKey(Models.SignalKeys.IntelHardGate),
                // Periodicity signals. PeriodicityContributor needs >=10 requests in the
                // signature's sliding history before any analysis runs, so short BDF
                // scenarios typically probe False. Long-run BDF + production both write
                // these. Critical for the API-key-theft case (sudden cadence change).
                [Models.SignalKeys.PeriodicityCV] = signals.ContainsKey(Models.SignalKeys.PeriodicityCV),
                [Models.SignalKeys.PeriodicityMeanInterval] = signals.ContainsKey(Models.SignalKeys.PeriodicityMeanInterval),
                [Models.SignalKeys.PeriodicityDominantPeriod] = signals.ContainsKey(Models.SignalKeys.PeriodicityDominantPeriod),
                [Models.SignalKeys.PeriodicityPeakStrength] = signals.ContainsKey(Models.SignalKeys.PeriodicityPeakStrength),
                [Models.SignalKeys.PeriodicityHourEntropy] = signals.ContainsKey(Models.SignalKeys.PeriodicityHourEntropy),
                // Identity-change risk signals. Only fire when the matched fingerprint
                // has a prior in-memory snapshot AND a surface dimension has shifted.
                // FOSS stub for the commercial API-protection feature.
                [Models.SignalKeys.RiskCountryChanged] = signals.ContainsKey(Models.SignalKeys.RiskCountryChanged),
                [Models.SignalKeys.RiskCountryTransition] = signals.ContainsKey(Models.SignalKeys.RiskCountryTransition),
                [Models.SignalKeys.RiskAsnChanged] = signals.ContainsKey(Models.SignalKeys.RiskAsnChanged),
                [Models.SignalKeys.RiskUaFamilyChanged] = signals.ContainsKey(Models.SignalKeys.RiskUaFamilyChanged),
                [Models.SignalKeys.RiskInfrastructureIntroduced] = signals.ContainsKey(Models.SignalKeys.RiskInfrastructureIntroduced),
                // Bonus A + BotD drift dims added by IdentityChangeContributor.
                // ShapeHash drift is the strongest single-dim signal (canvas+WebGL
                // is hardware-derived). BotdKind drift catches automation-framework
                // swap under the same identity.
                [Models.SignalKeys.RiskShapeHashChanged] = signals.ContainsKey(Models.SignalKeys.RiskShapeHashChanged),
                [Models.SignalKeys.RiskBotdKindChanged] = signals.ContainsKey(Models.SignalKeys.RiskBotdKindChanged),
                [Models.SignalKeys.RiskSuspiciousChangeScore] = signals.ContainsKey(Models.SignalKeys.RiskSuspiciousChangeScore),
                [Models.SignalKeys.RiskSuspiciousChangeReason] = signals.ContainsKey(Models.SignalKeys.RiskSuspiciousChangeReason),
                // This session's client-side beacon signals. ClientSideContributor
                // (priority 18) writes them from the stored BrowserFingerprintResult;
                // probes here surface them in dashboards / rig responses so operators
                // can audit which probes fired without reading the full Signals dict.
                [Models.SignalKeys.ClientSideConnectionType] = signals.ContainsKey(Models.SignalKeys.ClientSideConnectionType),
                [Models.SignalKeys.ClientSideIceNoSrflx] = signals.ContainsKey(Models.SignalKeys.ClientSideIceNoSrflx),
                [Models.SignalKeys.ClientSideTtsVoiceCount] = signals.ContainsKey(Models.SignalKeys.ClientSideTtsVoiceCount),
                [Models.SignalKeys.ClientSideBotdKind] = signals.ContainsKey(Models.SignalKeys.ClientSideBotdKind),
                [Models.SignalKeys.ClientSideShapeHash] = signals.ContainsKey(Models.SignalKeys.ClientSideShapeHash),
                [Models.SignalKeys.ClientSidePoolCollisionContexts] = signals.ContainsKey(Models.SignalKeys.ClientSidePoolCollisionContexts),
                [Models.SignalKeys.ClientSideMouseAllIntegerCoords] = signals.ContainsKey(Models.SignalKeys.ClientSideMouseAllIntegerCoords),
                [Models.SignalKeys.ClientSideMouseTimingCv] = signals.ContainsKey(Models.SignalKeys.ClientSideMouseTimingCv),
                // ClientMouseEvents was a ghost signal until Bonus B; probe makes the
                // fill explicit so a future regression that stops writing it is loud.
                [Models.SignalKeys.ClientMouseEvents] = signals.ContainsKey(Models.SignalKeys.ClientMouseEvents),
                // TLS subset / version-delta signals from Plan 2a. Probe-false when
                // no JA3 forwarding is configured; probe-true confirms the corpus
                // checks reached the comparison.
                [Models.SignalKeys.TlsCipherSubsetOfRealChrome] = signals.ContainsKey(Models.SignalKeys.TlsCipherSubsetOfRealChrome),
                [Models.SignalKeys.TlsVersionDeltaFromUa] = signals.ContainsKey(Models.SignalKeys.TlsVersionDeltaFromUa),
                // Async coordination signals. IdentityFingerprintFirstSeen fires on the allocate path
                // (brand-new fingerprint row); IdentityFingerprintObservationCountCrossed fires when
                // observation_count crosses a configured threshold; IdentityFingerprintMaturityCrossed
                // fires when centroid maturity crosses the configured gate. Absorption /
                // drift subscribers wake on these instead of polling the durable tier.
                [Models.SignalKeys.IdentityFingerprintFirstSeen] = signals.ContainsKey(Models.SignalKeys.IdentityFingerprintFirstSeen),
                [Models.SignalKeys.IdentityFingerprintObservationCountCrossed] = signals.ContainsKey(Models.SignalKeys.IdentityFingerprintObservationCountCrossed),
                [Models.SignalKeys.IdentityFingerprintMaturityCrossed] = signals.ContainsKey(Models.SignalKeys.IdentityFingerprintMaturityCrossed)
            };

            // Identity match outputs (null when Identity.Enabled = false)
            var identityFingerprintId = signals.TryGetValue(Models.SignalKeys.IdentityFingerprintId, out var fpObj)
                ? fpObj as string : null;
            var identityClientType = signals.TryGetValue(Models.SignalKeys.IdentityClientType, out var ctObj)
                ? ctObj as string : null;
            var identityIsNew = signals.TryGetValue(Models.SignalKeys.IdentityIsNewFingerprint, out var nfObj)
                && nfObj is bool nf && nf;
            var identityIsCorrection = signals.TryGetValue(Models.SignalKeys.IdentityIsCorrection, out var cObj)
                && cObj is bool c && c;

            var actual = new BdfReplayActual
            {
                IsBot = evidence.BotProbability >= 0.5,
                BotProbability = Math.Round(evidence.BotProbability, 4),
                BotType = evidence.PrimaryBotType?.ToString(),
                BotName = evidence.PrimaryBotName,
                RiskBand = evidence.RiskBand.ToString(),
                SignalCount = signals.Count,
                SignalProbes = signalProbes,
                IdentityFingerprintId = identityFingerprintId,
                IdentityClientType = identityClientType,
                IdentityIsNewFingerprint = identityIsNew,
                IdentityIsCorrection = identityIsCorrection,
                TopReasons = evidence.Contributions
                    .Where(c => !string.IsNullOrEmpty(c.Reason))
                    .OrderByDescending(c => Math.Abs(c.ConfidenceDelta))
                    .Take(5)
                    .Select(c => $"{c.DetectorName}: {c.Reason}")
                    .ToList()
            };

            // Compare with expected
            var isMatch = true;
            var detectedAsBot = evidence.BotProbability >= 0.5;
            if (req.ExpectedDetection != null)
            {
                if (req.ExpectedDetection.IsBot != detectedAsBot)
                {
                    isMatch = false;
                    if (detectedAsBot && !req.ExpectedDetection.IsBot)
                        falsePositives++;
                    else if (!detectedAsBot && req.ExpectedDetection.IsBot)
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

    // Single lock for the whole limiter: serialising the eviction path against
    // GetOrAdd closes the window where a concurrent eviction could remove the
    // entry another request just added for the same IP. Test-endpoint workload
    // is light, contention cost is negligible.
    private static readonly object RateLimitLock = new();

    private static bool CheckRateLimit(string clientIp, int maxPerMinute)
    {
        var now = DateTime.UtcNow;
        List<DateTime> window;

        lock (RateLimitLock)
        {
            if (RateLimitWindow.Count > MaxRateLimitEntries)
                foreach (var key in RateLimitWindow.Keys.Take(MaxRateLimitEntries / 2).ToList())
                    RateLimitWindow.TryRemove(key, out _);

            window = RateLimitWindow.GetOrAdd(clientIp, _ => new List<DateTime>());
        }

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
///     BDF replay request - subset of BDF v2 fields needed for replay.
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
    public string? BotName { get; set; }
    public string? RiskBand { get; set; }
    public int SignalCount { get; set; }
    public Dictionary<string, bool> SignalProbes { get; set; } = new();
    public string? IdentityFingerprintId { get; set; }
    public string? IdentityClientType { get; set; }
    public bool IdentityIsNewFingerprint { get; set; }
    public bool IdentityIsCorrection { get; set; }
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