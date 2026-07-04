using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Lifecycle;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that lifts the threat score when a
///     request hits a path that <em>used to serve real content</em>.
///     Native <see cref="IDetectorAtom"/> replacement for
///     <c>EndpointHistoryContributor</c>.
/// </summary>
/// <remarks>
///     <para>
///         The signal we're reading is "this path has institutional memory":
///         a 2xx history followed by a flip to 4xx, meaning the endpoint was
///         real, then removed, and a scanner is still probing for it. That's
///         qualitatively more dangerous than a scanner hitting a path that
///         never existed -- the attacker has a cached crawl, a leaked
///         credential dump, or an old documentation link.
///     </para>
///     <para>
///         Reads from <see cref="IPathLifecycleStore"/>, which is populated
///         by <see cref="PathLifecycleMiddleware"/> from PRIOR responses. The
///         lookup is async but bounded — typical hit is a cache read.
///     </para>
///     <para>
///         Raises <c>endpoint.threat_boost</c> (consumed by threat-score
///         extractors via the existing Math.Max chain — never replaces a
///         higher catalog-derived score), plus lifecycle metadata signals so
///         the deterministic explanation surfaces "was real until 2026-03-15".
///     </para>
/// </remarks>
public sealed class EndpointHistoryAtom : DetectorAtomBase
{
    public const string SignalHistoryMatch = "endpoint.history_match";
    public const string SignalLast2xxUtc = "endpoint.last_2xx_utc";
    public const string SignalFirst4xxAfter2xxUtc = "endpoint.first_4xx_after_2xx_utc";
    public const string SignalTotal2xx = "endpoint.total_2xx";

    /// <summary>Threat score floor published on a "formerly real" path hit.</summary>
    public const double HistoryThreatBoost = 0.65;

    private readonly ILogger<EndpointHistoryAtom> _logger;
    private readonly IPathLifecycleStore _store;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public EndpointHistoryAtom(
        ILogger<EndpointHistoryAtom> logger,
        IPathLifecycleStore store,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "EndpointHistory", category: "EndpointHistory")
    {
        _logger = logger;
        _store = store;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>Wave 0, after HoneypotLink.</summary>
    public override int Priority => 6;

    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path)) return None();

        var lifecycle = await _store.GetAsync(path, ct).ConfigureAwait(false);
        if (lifecycle is null || !lifecycle.IsFormerlyReal) return None();

        sink.Raise($"{SignalHistoryMatch}:true", sessionId);
        sink.Raise($"{SignalKeys.IntentThreatScore}:{HistoryThreatBoost.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalTotal2xx}:{lifecycle.Total2xx}", sessionId);
        if (lifecycle.Last2xxUtc.HasValue)
            sink.Raise($"{SignalLast2xxUtc}:{lifecycle.Last2xxUtc.Value:O}", sessionId);
        if (lifecycle.First4xxAfter2xxUtc.HasValue)
            sink.Raise($"{SignalFirst4xxAfter2xxUtc}:{lifecycle.First4xxAfter2xxUtc.Value:O}", sessionId);

        var lastReal = lifecycle.Last2xxUtc?.ToString("yyyy-MM-dd") ?? "?";
        var reason = $"Endpoint history: {path} served 2xx until {lastReal} ({lifecycle.Total2xx} hits); scanners still probing it";

        _logger.LogInformation(
            "Formerly-real endpoint probed: {Path} last 2xx {LastReal}, total 2xx {T2}",
            path, lastReal, lifecycle.Total2xx);

        return Single(DetectionContribution.Bot(
            Name,
            "EndpointHistory",
            0.70,
            reason,
            1.5,
            nameof(BotType.Scraper)));
    }
}
