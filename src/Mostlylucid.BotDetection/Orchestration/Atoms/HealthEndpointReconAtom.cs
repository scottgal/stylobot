using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.HealthEndpoints;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (Priority 16) that raises <see cref="SignalKeys.HealthEndpointRecon"/>
///     and nudges <see cref="SignalKeys.IntentThreatScore"/> when a health / probe endpoint
///     is hit by a request that is NOT a legitimate expected probe.
/// </summary>
/// <remarks>
///     <para>
///         Runs in Wave 1 (after Wave 0): <see cref="RequiredSignals"/> lists
///         <c>request.health_endpoint</c> which is raised by
///         <see cref="HealthEndpointAtom"/> (Priority 2, Wave 0). Because
///         <c>sink.Detect</c> uses exact string matching and the signal is stored
///         as <c>request.health_endpoint:true</c>, the required-signal check returns
///         false on the initial sink, guaranteeing this atom lands in the later wave
///         where both <c>HealthEndpointAtom</c> and <c>IpAtom</c> (Priority 12) have
///         already written their signals.
///     </para>
///     <para>
///         Probe legitimacy is a conjunction of two conditions (matching Task 3's
///         shape-AND-source rule):
///         <list type="bullet">
///             <item><description>Source: <c>ip.is_local</c> is true.</description></item>
///             <item>
///                 <description>Shape: <see cref="ProbeShapeClassifier.IsProbeShape"/> is true
///                 (probe-family UA token present AND no browser-navigation
///                 <c>Sec-Fetch-Mode:navigate</c>).
///                 </description>
///             </item>
///         </list>
///         ANY other combination (external source, or local source with browser shape)
///         is treated as reconnaissance.
///     </para>
///     <para>
///         The nudge magnitude (<see cref="ReconNudge"/>) is small and deliberate:
///         health-endpoint recon is a soft signal, not a block signal. It stacks
///         additively (via Math.Max) with other threat contributions in
///         <c>DetectionLedgerExtensions.ExtractThreatScore</c> so that co-occurring
///         recon patterns compound without any single signal dominating.
///     </para>
/// </remarks>
public sealed class HealthEndpointReconAtom : DetectorAtomBase
{
    /// <summary>
    ///     Threat-score nudge published on a health-endpoint recon hit.
    ///     Small by design: health-endpoint recon is a soft, composable signal.
    ///     Placed at 0.35 (Elevated band floor) so it contributes meaningfully
    ///     alongside other recon signals without triggering a solo block.
    /// </summary>
    public const double ReconNudge = 0.35;

    private readonly ILogger<HealthEndpointReconAtom> _logger;
    private readonly HealthEndpointCatalog _catalog;
    private readonly IOptions<HealthEndpointOptions> _healthOptions;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HealthEndpointReconAtom(
        ILogger<HealthEndpointReconAtom> logger,
        HealthEndpointCatalog catalog,
        IOptions<HealthEndpointOptions> healthOptions,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "HealthEndpointRecon", category: "Request")
    {
        _logger = logger;
        _catalog = catalog;
        _healthOptions = healthOptions;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    ///     Priority 16: after IpAtom (12) and ProjectHoneypotAtom (15) so that
    ///     <c>ip.is_local</c> is available before the local/external branch runs.
    /// </summary>
    public override int Priority => 16;

    /// <summary>
    ///     Listing <c>request.health_endpoint</c> here forces this atom into Wave 1
    ///     (after Wave 0 completes). The exact-match <c>Detect</c> call inside
    ///     <c>AllSignalsSatisfied</c> returns false for <c>request.health_endpoint:true</c>,
    ///     so the atom is deferred until <c>HealthEndpointAtom</c> and <c>IpAtom</c>
    ///     have already written their signals.
    /// </summary>
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.HealthEndpoint };

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var isHealthEndpoint = sink.ReadBoolHint(SignalKeys.HealthEndpoint);
        if (!isHealthEndpoint)
            return Task.FromResult(None());

        var isLocal = sink.ReadBoolHint(SignalKeys.IpIsLocal);
        var probeUas = _healthOptions.Value.ProbeUserAgents;
        var isProbeShape = ProbeShapeClassifier.IsProbeShape(
            signals: new Dictionary<string, object>(),
            sink: sink,
            probeUserAgents: probeUas);

        if (isLocal && isProbeShape)
        {
            // Legitimate health probe: local source + probe shape. Not recon.
            _logger.LogDebug("Health probe confirmed as legitimate (local + probe shape): no recon signal raised");
            return Task.FromResult(None());
        }

        // External source OR local source with browser/non-probe shape = reconnaissance.
        var reconReason = isLocal
            ? "Health endpoint hit from local IP with browser/non-probe shape"
            : "Health endpoint hit from external (non-local) source";

        sink.Raise($"{SignalKeys.HealthEndpointRecon}:true", sessionId);
        sink.Raise($"{SignalKeys.IntentThreatScore}:{ReconNudge.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

        _logger.LogInformation(
            "Health endpoint recon detected: {Reason}; nudging intent.threat_score to {Nudge:F2}",
            reconReason, ReconNudge);

        return Task.FromResult(Single(
            DetectionContribution.Info(Name, Category, reconReason)));
    }
}
