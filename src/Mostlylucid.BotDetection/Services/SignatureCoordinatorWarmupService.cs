using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Replays recently persisted request records into the in-memory cross-request
///     coordinator on startup. This prevents clustering from starting from zero after
///     a restart when request persistence is enabled.
/// </summary>
public sealed class SignatureCoordinatorWarmupService : BackgroundService
{
    private readonly IDetectionArchive _store;
    private readonly SignatureCoordinator _signatureCoordinator;
    private readonly MarkovTracker? _markovTracker;
    private readonly BotDetectionOptions _options;
    private readonly ILogger<SignatureCoordinatorWarmupService> _logger;

    public SignatureCoordinatorWarmupService(
        IDetectionArchive store,
        SignatureCoordinator signatureCoordinator,
        IOptions<BotDetectionOptions> options,
        ILogger<SignatureCoordinatorWarmupService> logger,
        MarkovTracker? markovTracker = null)
    {
        _store = store;
        _signatureCoordinator = signatureCoordinator;
        _options = options.Value;
        _logger = logger;
        _markovTracker = markovTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var coordinatorOptions = _options.SignatureCoordinator;
            var limit = coordinatorOptions.MaxSignaturesInWindow *
                        Math.Min(coordinatorOptions.MaxRequestsPerSignature, 20);
            var since = DateTime.UtcNow - coordinatorOptions.SignatureWindow;
            var requests = await _store.GetRecentRequestsAsync(limit, since, stoppingToken);

            foreach (var request in requests)
            {
                stoppingToken.ThrowIfCancellationRequested();

                await _signatureCoordinator.RecordRequestAsync(
                    request.Signature,
                    request.Id > 0 ? $"persisted-{request.Id}" : Guid.NewGuid().ToString("N"),
                    request.Path,
                    request.BotProbability,
                    new Dictionary<string, object>
                    {
                        ["path"] = request.Path,
                        ["persisted.status_code"] = request.StatusCode,
                        ["persisted.risk_band"] = request.RiskBand
                    },
                    new HashSet<string> { "persisted" },
                    stoppingToken,
                    timestampUtc: request.Timestamp);

                _markovTracker?.RecordTransition(
                    request.Signature,
                    request.Path,
                    request.Timestamp,
                    request.BotProbability > 0.5,
                    isDatacenter: false,
                    isReturning: true);
            }

            if (requests.Count > 0)
                _logger.LogInformation(
                    "Replayed {Count} persisted requests into SignatureCoordinator warmup window",
                    requests.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignatureCoordinator warmup failed; clustering will start from live traffic only");
        }
    }
}
