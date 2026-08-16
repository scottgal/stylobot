using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.InternalPlumbing;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Boundary SensorAtom (Wave 0, Priority 2) that raises
///     <see cref="SignalKeys.InternalPlumbing"/> when the inbound request path is one of
///     the product's OWN plumbing endpoints — the SignalR dashboard hub and the
///     client-side fingerprint beacon.
/// </summary>
/// <remarks>
///     <para>
///         Matching delegates to <see cref="InternalPlumbingCatalog"/> which does a
///         case-insensitive segment-boundary prefix check (so <c>/stylobot/hub/negotiate</c>
///         also matches the <c>/stylobot/hub</c> prefix, but <c>/stylobot/hubspot</c> does
///         not).
///     </para>
///     <para>
///         When the signal is raised, the ledger classifies the request
///         <see cref="BotType.Internal"/> (the same semantics as the LAN-trust carve-out:
///         the product's own plumbing can never read as a high-threat visitor, and
///         Internal verdicts are excluded from the visitor risk feed).
///     </para>
///     <para>
///         Priority 2 keeps this atom ahead of all scoring atoms (FastPathReputation
///         is Priority 3) so the signal is available to any atom that needs it.
///     </para>
/// </remarks>
public sealed class InternalPlumbingAtom : DetectorAtomBase
{
    private readonly ILogger<InternalPlumbingAtom> _logger;
    private readonly InternalPlumbingCatalog _catalog;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public InternalPlumbingAtom(
        ILogger<InternalPlumbingAtom> logger,
        InternalPlumbingCatalog catalog,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "InternalPlumbing", category: "Request")
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
        if (!_catalog.IsInternalPlumbingPath(path))
            return Task.FromResult(None());

        sink.Raise($"{SignalKeys.InternalPlumbing}:true", sessionId);
        _logger.LogDebug("Internal plumbing path recognised: {Path}", path);

        return Task.FromResult(Single(
            DetectionContribution.Info(Name, Category, $"Product's own plumbing path: {path}")));
    }
}
