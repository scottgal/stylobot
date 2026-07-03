using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Pulls raw unatomized requests out of the session store, slices them
///     into session groups by gap, vectorises and persists the resulting
///     session rows. Runs on the project-wide
///     <see cref="IScheduleCoordinator"/> Tick1m cadence, gated on
///     "last-success older than configured
///     <see cref="RetentionOptions.AtomizerRunInterval"/>" (default 2 min).
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(RunInterval)</c> loop and a shutdown forceFlush;
///         the shutdown flush is intentionally dropped -- unatomized
///         requests stay in SQLite and are picked up on the next boot's
///         first tick (per the migration-plan risk #3: "the next tick
///         re-reads the same window"). See
///         <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
public sealed class SessionAtomizerService : IDisposable
{
    private const int BatchLimit = 5000;

    private readonly ISessionStore _store;
    private readonly ILogger<SessionAtomizerService> _logger;
    private readonly IOptionsMonitor<BotDetectionOptions> _options;
    private readonly IDisposable _subscription;
    private DateTime _lastSuccessfulPassUtc = DateTime.MinValue;
    private int _disposed;

    private RetentionOptions      Retention      => _options.CurrentValue.Retention;
    private ClassificationOptions Classification => _options.CurrentValue.Classification;
    private TimeSpan SessionGap     => Retention.SessionGap;
    private TimeSpan RunInterval    => Retention.AtomizerRunInterval;
    private TimeSpan GraceAge       => Retention.SessionGraceAge;
    private int      MinRequests    => Retention.SessionMinRequests;

    public SessionAtomizerService(
        ISessionStore store,
        ILogger<SessionAtomizerService> logger,
        IOptionsMonitor<BotDetectionOptions> options,
        IScheduleCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _store = store;
        _logger = logger;
        _options = options;

        _subscription = coordinator.Subscribe(
            TickCadence.Tick1m,
            "SessionAtomizerService",
            CostHint.Medium,
            OnTickAsync);
    }

    /// <summary>
    ///     The coordinator's tick handler. Fires every Tick1m; gates the
    ///     atomize pass on "last-success older than configured
    ///     <see cref="RetentionOptions.AtomizerRunInterval"/>" so the actual
    ///     pass cadence honours the option (default 2 minutes -> pass runs
    ///     every other tick). Public so tests can drive a single beat
    ///     deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        if (_lastSuccessfulPassUtc != DateTime.MinValue &&
            now.UtcDateTime - _lastSuccessfulPassUtc < RunInterval)
        {
            return; // Not yet due.
        }

        try
        {
            await AtomizePassAsync(forceFlush: false, ct);
            _lastSuccessfulPassUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionAtomizerService pass failed");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }
    }

    private async Task AtomizePassAsync(bool forceFlush, CancellationToken ct)
    {
        var requests = await _store.GetUnatomizedRequestsAsync(BatchLimit, ct);
        if (requests.Count == 0) return;

        var now = DateTime.UtcNow;
        var sessionized = 0;

        foreach (var sigGroup in requests.GroupBy(r => r.Signature))
        {
            var ordered  = sigGroup.OrderBy(r => r.Timestamp).ToList();
            var sessions = SplitIntoSessionGroups(ordered, now, forceFlush, SessionGap, GraceAge);

            foreach (var group in sessions)
            {
                if (group.Count < MinRequests) continue;

                var sessionRequests = group
                    .Select(r => new SessionRequest(
                        Enum.TryParse<RequestState>(r.MarkovState, out var s) ? s : RequestState.PageView,
                        new DateTimeOffset(r.Timestamp, TimeSpan.Zero),
                        r.Path,
                        r.StatusCode))
                    .ToList();

                var vector   = SessionVectorizer.Encode(sessionRequests, null);
                var maturity = SessionVectorizer.ComputeMaturity(sessionRequests);
                var dominant = sessionRequests
                    .GroupBy(r => r.State)
                    .OrderByDescending(g => g.Count())
                    .First().Key;

                var avgBot  = group.Average(r => r.BotProbability);
                var avgConf = group.Average(r => r.Confidence);
                var riskBand = group.OrderByDescending(r => r.BotProbability).First().RiskBand;

                // Build transition counts for Markov drill-in visualization
                var transitionCounts = new Dictionary<string, int>();
                for (var i = 1; i < sessionRequests.Count; i++)
                {
                    var key = $"{sessionRequests[i - 1].State}->{sessionRequests[i].State}";
                    transitionCounts[key] = transitionCounts.GetValueOrDefault(key) + 1;
                }

                // Collect distinct templatized paths
                var paths = sessionRequests.Select(r => r.PathTemplate).Distinct().ToList();

                // Derive scope from the most recent request in the group -- background
                // atomizer runs off the request pipeline so there's no HttpContext to
                // read RequestScope from. The per-request Domain/Host stamped at
                // AddRequestBatchAsync time is the authoritative source.
                var latest = group[^1];
                var groupScope = new RequestScope(
                    latest.Domain ?? RequestScope.Unknown.Domain,
                    latest.Host ?? RequestScope.Unknown.Host);

                var session = new PersistedSession
                {
                    Domain               = latest.Domain,
                    Host                 = latest.Host,
                    Signature            = sigGroup.Key,
                    StartedAt            = group.Min(r => r.Timestamp),
                    EndedAt              = group.Max(r => r.Timestamp),
                    RequestCount         = group.Count,
                    Vector               = SqliteSessionStore.SerializeVector(vector),
                    Maturity             = maturity,
                    DominantState        = dominant.ToString(),
                    IsBot                = avgBot > 0.5,
                    AvgBotProbability    = avgBot,
                    AvgConfidence        = avgConf,
                    RiskBand             = riskBand,
                    AvgProcessingTimeMs  = group.Average(r => r.ProcessingMs),
                    ErrorCount           = group.Count(r => r.StatusCode is >= 400 and < 600),
                    TimingEntropy        = ComputeTimingEntropy(group),
                    TransitionCountsJson = JsonSerializer.Serialize(transitionCounts, Data.BotDetectionJsonSerializerContext.Default.DictionaryStringInt32),
                    PathsJson            = JsonSerializer.Serialize(paths, Data.BotDetectionJsonSerializerContext.Default.ListString),
                };

                var sessionId = await _store.AddSessionAsync(groupScope, session, ct);
                await _store.LinkRequestsToSessionAsync(sessionId, group.Select(r => r.Id).ToList(), ct);
                sessionized++;
            }
        }

        if (sessionized > 0)
            _logger.LogInformation("Atomizer: {Sessions} sessions from {Requests} requests",
                sessionized, requests.Count);
    }

    internal static IReadOnlyList<List<PersistedRequest>> SplitIntoSessionGroups(
        List<PersistedRequest> ordered, DateTime now, bool forceFlush,
        TimeSpan sessionGap, TimeSpan graceAge)
    {
        var groups  = new List<List<PersistedRequest>>();
        var current = new List<PersistedRequest> { ordered[0] };

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Timestamp - ordered[i - 1].Timestamp >= sessionGap)
            { groups.Add(current); current = new List<PersistedRequest>(); }
            current.Add(ordered[i]);
        }

        var lastTs = current.Max(r => r.Timestamp);
        if (forceFlush || (now - lastTs) >= graceAge)
            groups.Add(current);

        return groups;
    }

    private static float ComputeTimingEntropy(List<PersistedRequest> requests)
    {
        if (requests.Count < 2) return 0f;
        var intervals = requests
            .Zip(requests.Skip(1), (a, b) => (b.Timestamp - a.Timestamp).TotalMilliseconds)
            .ToList();
        var total = (double)intervals.Count;
        double entropy = 0;
        foreach (var count in intervals.GroupBy(ms => (int)(ms / 100)).Select(g => g.Count()))
        {
            var p = count / total;
            if (p > 0) entropy -= p * Math.Log2(p);
        }
        return (float)entropy;
    }
}
