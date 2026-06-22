using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Operator admin endpoints used by the setup flow to apply config changes without
///     a manual container restart.
///     <para>
///     <c>POST {BasePath}/reload</c> -- triggers <see cref="IConfigurationRoot.Reload"/>;
///     IOptionsMonitor consumers pick up the new values on their next read. Returns 200.
///     </para>
///     <para>
///     <c>POST {BasePath}/restart</c> -- requests graceful shutdown via
///     <see cref="IHostApplicationLifetime.StopApplication"/>; the supervisor
///     (Docker, systemd, launchctl) starts a fresh process. Returns 202.
///     </para>
///     <para>
///     Auth: <c>Authorization: Bearer &lt;token&gt;</c> where the token matches
///     <see cref="AdminOptions.Token"/>. Comparison is constant-time. There is no
///     anonymous fallback -- when the token is unset the endpoints return 401
///     with a body that tells the operator to configure
///     <c>StyloBot:Dashboard:Admin:Token</c>. Requests never reach the reload or
///     restart handlers without a matching bearer.
///     </para>
/// </summary>
public sealed class StyloBotAdminMiddleware
{
    private readonly RequestDelegate _next;
    private readonly StyloBotDashboardOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<StyloBotAdminMiddleware> _logger;

    public StyloBotAdminMiddleware(
        RequestDelegate next,
        StyloBotDashboardOptions options,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<StyloBotAdminMiddleware> logger)
    {
        _next = next;
        _options = options;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var basePath = _options.Admin.BasePath.TrimEnd('/');
        var path = context.Request.Path.Value ?? string.Empty;
        var token = _options.Admin.Token;

        // Anything off the admin base path is none of our business.
        if (!path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Disabled by default. Operator must explicitly set Admin:Enabled=true.
        // When disabled, admin paths fall through to the rest of the pipeline (so
        // they 404 like any other unmatched path -- no admin surface advertised).
        if (!_options.Admin.Enabled)
        {
            await _next(context);
            return;
        }

        // POST is the default verb for reload/restart (state-mutating). GET is allowed
        // only for the read-only learning/health endpoint -- operators tend to curl
        // it without flags and 405-ing them on GET is hostile when the endpoint
        // produces JSON anyway.
        var subPathPeek = path[(basePath.Length + 1)..].TrimEnd('/').ToLowerInvariant();
        var isReadOnlyEndpoint = subPathPeek == "learning/health";
        var methodOk = isReadOnlyEndpoint
            ? (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsPost(context.Request.Method))
            : HttpMethods.IsPost(context.Request.Method);
        if (!methodOk)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers["Allow"] = isReadOnlyEndpoint ? "GET, POST" : "POST";
            return;
        }

        // No anonymous path through. If the operator hasn't configured a token, the
        // admin endpoints are unreachable -- 401 with a body that points at the
        // exact config key so the operator knows what to set instead of guessing.
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning(
                "Admin request rejected: no AdminToken configured ({Path} from {IP})",
                path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"stylobot-admin\"";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"status\":\"admin_disabled\"," +
                "\"message\":\"Set StyloBot:Dashboard:Admin:Token to enable admin endpoints.\"}");
            return;
        }

        if (!TryAuthorize(context, token))
        {
            _logger.LogWarning("Admin request rejected: {Path} from {IP}",
                path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"stylobot-admin\"";
            return;
        }

        var subPath = path[(basePath.Length + 1)..];
        switch (subPath.TrimEnd('/').ToLowerInvariant())
        {
            case "reload":
                await HandleReloadAsync(context);
                return;
            case "restart":
                await HandleRestartAsync(context);
                return;
            case "learning/health":
                await HandleLearningHealthAsync(context);
                return;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    /// <summary>
    ///     GET-shaped learning observability endpoint. Returns the calibration
    ///     service's last trigger decision + the per-archetype drift metric
    ///     snapshot from the most recent calibration pass. The middleware otherwise
    ///     requires POST, so this case allows GET-or-POST (operators tend to curl
    ///     it without flags).
    /// </summary>
    private async Task HandleLearningHealthAsync(HttpContext context)
    {
        var calibration = context.RequestServices
            .GetService<IdentityWeightCalibrationService>();
        var archetypeRegistry = context.RequestServices
            .GetService<IdentityArchetypeRegistry>();

        if (calibration is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"status\":\"identity_disabled\",\"message\":\"IdentityWeightCalibrationService is not registered. Set BotDetection:Identity:Enabled = true.\"}");
            return;
        }

        var snapshot = calibration.LastDecisionSnapshot;
        var metrics = calibration.LastDriftMetrics;
        var shrinks = calibration.LastShrinkActions;
        var archetypes = archetypeRegistry?.All ?? Array.Empty<IdentityArchetype>();

        var body = new
        {
            calibration = new
            {
                last_decision_at = snapshot.At == DateTimeOffset.MinValue
                    ? null
                    : snapshot.At.ToString("O"),
                should_run = snapshot.ShouldRun,
                reason = snapshot.Reason,
                signals = snapshot.Signals,
            },
            drift_metrics = new
            {
                row_count = metrics.Count,
                rows = metrics.Select(m => new
                {
                    archetype_id = m.ArchetypeId,
                    ua_family = m.UaFamily,
                    matches_asserted_ua = m.MatchesAssertedUa,
                    descendant_count = m.DescendantCount,
                    mean_l2 = m.MeanL2ToCentroid,
                    variance_l2 = m.VarianceL2ToCentroid,
                    p90_l2 = m.P90L2ToCentroid,
                    calibrated_at = m.CalibratedAt.ToString("O"),
                }),
            },
            umbrella_shrinkage = new
            {
                row_count = shrinks.Count,
                rows = shrinks.Select(kv => new
                {
                    archetype_id = kv.Key,
                    previous_multiplier = kv.Value.PreviousMultiplier,
                    new_multiplier = kv.Value.NewMultiplier,
                    bloat = kv.Value.Bloat,
                    leakage_descendant_count = kv.Value.LeakageDescendantCount,
                    split_candidate = kv.Value.SplitCandidate,
                }),
            },
            // Phase 4 (spec §2.D) centroid mobility view. One row per archetype
            // that the registry currently holds, with the diagnostics the
            // maintainer asked for: "is it pinned, is it moving, is it getting
            // close to a neighbour". Top-N by pin_cycles surfaces the suspicious
            // entries first.
            centroid_mobility = new
            {
                row_count = archetypes.Count,
                top_pinned = archetypes
                    .Where(a => a.PinCycles > 0)
                    .OrderByDescending(a => a.PinCycles)
                    .Take(20)
                    .Select(a => new
                    {
                        archetype_id = a.ArchetypeId,
                        pin_cycles = a.PinCycles,
                        centroid_delta_last_cycle = a.CentroidDeltaLastCycle,
                        descendant_variance_last_cycle = a.DescendantVarianceLastCycle,
                        variance_multiplier = a.VarianceMultiplier,
                        nearest_neighbour_id = a.NearestNeighbourId,
                        nearest_neighbour_distance = a.NearestNeighbourDistance,
                    }),
                top_proximate = archetypes
                    .Where(a => a.NearestNeighbourId is not null && a.NearestNeighbourDistance > 0)
                    .OrderBy(a => a.NearestNeighbourDistance)
                    .Take(20)
                    .Select(a => new
                    {
                        archetype_id = a.ArchetypeId,
                        nearest_neighbour_id = a.NearestNeighbourId,
                        nearest_neighbour_distance = a.NearestNeighbourDistance,
                        descendant_count = a.DescendantCount,
                    }),
            },
            // Phase 5 v3 (spec §2.D rule 3): pairs that have stayed under the
            // convergence threshold for several cycles. This is the dashboard
            // surface that tells operators "two archetypes are about to merge --
            // diverge them in YAML or accept the merge". top_proximate is the
            // snapshot; merge_candidates is the aging warning.
            convergence = new
            {
                row_count = calibration.LastConvergenceCandidates.Count,
                merge_candidates = calibration.LastConvergenceCandidates.Select(c => new
                {
                    archetype_a = c.ArchetypeA,
                    archetype_b = c.ArchetypeB,
                    distance = c.Distance,
                    consecutive_cycles = c.Cycles,
                }),
            },
        };

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, body,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false,
            });
    }

    private async Task HandleReloadAsync(HttpContext context)
    {
        if (_configuration is IConfigurationRoot root)
        {
            root.Reload();
            _logger.LogInformation("Admin reload: configuration reloaded by {IP}",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"status\":\"reloaded\"}");
            return;
        }

        _logger.LogWarning("Admin reload requested but IConfiguration is not IConfigurationRoot ({Type})",
            _configuration.GetType().FullName);
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"status\":\"reload_unsupported\"}");
    }

    private async Task HandleRestartAsync(HttpContext context)
    {
        _logger.LogWarning("Admin restart requested by {IP}; signalling graceful shutdown",
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"status\":\"restarting\"}");
        await context.Response.Body.FlushAsync();

        // Fire-and-forget so the response actually leaves the wire before the host begins
        // tearing the pipeline down. Without the small delay clients see a torn connection
        // instead of the 202.
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            _lifetime.StopApplication();
        });
    }

    private static bool TryAuthorize(HttpContext context, string expected)
    {
        if (!context.Request.Headers.TryGetValue("Authorization", out var header))
            return false;
        var raw = header.ToString();
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var provided = raw[prefix.Length..].Trim();
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
