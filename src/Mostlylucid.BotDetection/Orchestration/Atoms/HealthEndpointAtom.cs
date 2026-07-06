using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.HealthEndpoints;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Boundary SensorAtom (Wave 0, Priority 2) that raises
///     <see cref="SignalKeys.HealthEndpoint"/> when the inbound request path
///     is a recognised health / readiness / liveness probe endpoint.
/// </summary>
/// <remarks>
///     <para>
///         Matching delegates to <see cref="HealthEndpointCatalog"/> which does a
///         case-insensitive segment-boundary prefix check (so <c>/health/liveness</c>
///         also matches the <c>/health</c> prefix, but <c>/healthcheck</c> does not).
///     </para>
///     <para>
///         When the signal is raised, downstream classifiers and action-policy
///         consumers can gate on <c>request.health_endpoint:true</c> to apply
///         a neutral / pass-through posture for probe traffic without suppressing
///         detection on any other path.
///     </para>
///     <para>
///         Priority 2 keeps this atom ahead of all scoring atoms (FastPathReputation
///         is Priority 3) so the signal is available to any atom that needs to
///         skip heavy analysis for probe paths.
///     </para>
/// </remarks>
public sealed class HealthEndpointAtom : DetectorAtomBase
{
    private readonly ILogger<HealthEndpointAtom> _logger;
    private readonly HealthEndpointCatalog _catalog;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HealthEndpointAtom(
        ILogger<HealthEndpointAtom> logger,
        HealthEndpointCatalog catalog,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "HealthEndpoint", category: "Request")
    {
        _logger = logger;
        _catalog = catalog;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 2;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
            return Task.FromResult(None());

        var path = context.Request.Path;
        if (!_catalog.IsHealthPath(path))
            return Task.FromResult(None());

        sink.Raise($"{SignalKeys.HealthEndpoint}:true", sessionId);
        _logger.LogDebug("Health endpoint path recognised: {Path}", path);

        return Task.FromResult(Single(
            DetectionContribution.Info(Name, Category, $"Health probe path: {path}")));
    }
}
