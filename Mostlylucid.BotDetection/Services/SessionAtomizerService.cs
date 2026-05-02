using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Services;

public sealed class SessionAtomizerService : BackgroundService
{
    private static readonly TimeSpan SessionGap  = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RunInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GraceAge    = TimeSpan.FromMinutes(35);
    private const int MinRequests = 3;
    private const int BatchLimit  = 5000;

    private readonly ISessionStore _store;
    private readonly ILogger<SessionAtomizerService> _logger;

    public SessionAtomizerService(ISessionStore store, ILogger<SessionAtomizerService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SessionAtomizerService started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await AtomizePassAsync(forceFlush: false, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "SessionAtomizerService pass failed"); }
            await Task.Delay(RunInterval, stoppingToken).ConfigureAwait(false);
        }
        try { await AtomizePassAsync(forceFlush: true, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "SessionAtomizerService shutdown flush failed"); }
        _logger.LogInformation("SessionAtomizerService stopped");
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
            var sessions = SplitIntoSessionGroups(ordered, now, forceFlush);

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

                var session = new PersistedSession
                {
                    Signature           = sigGroup.Key,
                    StartedAt           = group.Min(r => r.Timestamp),
                    EndedAt             = group.Max(r => r.Timestamp),
                    RequestCount        = group.Count,
                    Vector              = SqliteSessionStore.SerializeVector(vector),
                    Maturity            = maturity,
                    DominantState       = dominant.ToString(),
                    IsBot               = avgBot > 0.5,
                    AvgBotProbability   = avgBot,
                    AvgConfidence       = avgConf,
                    RiskBand            = riskBand,
                    AvgProcessingTimeMs = group.Average(r => r.ProcessingMs),
                    ErrorCount          = group.Count(r => r.StatusCode is >= 400 and < 600),
                    TimingEntropy       = ComputeTimingEntropy(group),
                };

                var sessionId = await _store.AddSessionAsync(session, ct);
                await _store.LinkRequestsToSessionAsync(sessionId, group.Select(r => r.Id).ToList(), ct);
                sessionized++;
            }
        }

        if (sessionized > 0)
            _logger.LogInformation("Atomizer: {Sessions} sessions from {Requests} requests",
                sessionized, requests.Count);
    }

    internal static IReadOnlyList<List<PersistedRequest>> SplitIntoSessionGroups(
        List<PersistedRequest> ordered, DateTime now, bool forceFlush)
    {
        var groups  = new List<List<PersistedRequest>>();
        var current = new List<PersistedRequest> { ordered[0] };

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Timestamp - ordered[i - 1].Timestamp >= SessionGap)
            { groups.Add(current); current = new List<PersistedRequest>(); }
            current.Add(ordered[i]);
        }

        var lastTs = current.Max(r => r.Timestamp);
        if (forceFlush || (now - lastTs) >= GraceAge)
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
