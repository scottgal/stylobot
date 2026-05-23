using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Lifecycle;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Honeypot;

/// <summary>
///     Wave 0 contributor that lifts the threat score when a request hits a
///     path that <em>used to serve real content</em>.
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
///         by <see cref="PathLifecycleMiddleware"/> from PRIOR responses.
///         The lookup is async but bounded -- typical hit is a cache read.
///     </para>
///     <para>
///         Writes <c>endpoint.threat_boost</c> consumed by
///         <c>DetectionLedgerExtensions.ExtractThreatScore</c> via the
///         existing Math.Max chain; never replaces a higher catalog-derived
///         score. Also writes lifecycle metadata signals so the deterministic
///         explanation can surface "was real until 2026-03-15".
///     </para>
/// </remarks>
public sealed class EndpointHistoryContributor : ContributingDetectorBase
{
    public const string SignalHistoryMatch = "endpoint.history_match";
    public const string SignalLast2xxUtc = "endpoint.last_2xx_utc";
    public const string SignalFirst4xxAfter2xxUtc = "endpoint.first_4xx_after_2xx_utc";
    public const string SignalTotal2xx = "endpoint.total_2xx";

    /// <summary>Threat score floor we publish on a "formerly real" path hit.</summary>
    public const double HistoryThreatBoost = 0.65;

    private readonly ILogger<EndpointHistoryContributor> _logger;
    private readonly IPathLifecycleStore _store;

    public EndpointHistoryContributor(
        ILogger<EndpointHistoryContributor> logger,
        IPathLifecycleStore store)
    {
        _logger = logger;
        _store = store;
    }

    public override string Name => "EndpointHistory";

    /// <summary>Wave 0, after HoneypotLink. Both write threat-score signals; the extractor max's them.</summary>
    public override int Priority => 6;

    public override TimeSpan ExecutionTimeout => TimeSpan.FromMilliseconds(15);

    public override bool IsOptional => true;

    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var http = state.HttpContext;
        if (http == null) return None();

        var path = http.Request.Path.Value;
        if (string.IsNullOrEmpty(path)) return None();

        var lifecycle = await _store.GetAsync(path, cancellationToken);
        if (lifecycle is null || !lifecycle.IsFormerlyReal) return None();

        var signals = ImmutableDictionary.CreateBuilder<string, object>();
        signals.Add(SignalHistoryMatch, true);
        signals.Add(SignalKeys.IntentThreatScore, HistoryThreatBoost);
        signals.Add(SignalTotal2xx, lifecycle.Total2xx);
        if (lifecycle.Last2xxUtc.HasValue)
            signals.Add(SignalLast2xxUtc, lifecycle.Last2xxUtc.Value.ToString("O"));
        if (lifecycle.First4xxAfter2xxUtc.HasValue)
            signals.Add(SignalFirst4xxAfter2xxUtc, lifecycle.First4xxAfter2xxUtc.Value.ToString("O"));

        var lastReal = lifecycle.Last2xxUtc?.ToString("yyyy-MM-dd") ?? "?";
        var reason = $"Endpoint history: {path} served 2xx until {lastReal} ({lifecycle.Total2xx} hits); scanners still probing it";

        _logger.LogInformation(
            "Formerly-real endpoint probed: {Path} last 2xx {LastReal}, total 2xx {T2}",
            path, lastReal, lifecycle.Total2xx);

        return
        [
            DetectionContribution.Bot(
                Name,
                "EndpointHistory",
                0.70,
                reason,
                1.5,
                nameof(BotType.Scraper))
                with
                {
                    Signals = signals.ToImmutable()
                }
        ];
    }
}
