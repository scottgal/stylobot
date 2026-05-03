using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that generates LLM-based descriptions for signatures once they reach a request threshold.
///     Monitors signature activity and triggers description synthesis when request count exceeds the configured limit.
/// </summary>
public class SignatureDescriptionService : BackgroundService
{
    private readonly ILogger<SignatureDescriptionService> _logger;
    private readonly LlmDescriptionCoordinator _coordinator;
    private readonly int _requestThreshold;
    private readonly ConcurrentDictionary<string, SignatureActivityTracker> _signatureActivity;

    private record SignatureActivityTracker(string Signature, int RequestCount, Dictionary<string, object?> LatestSignals);

    public SignatureDescriptionService(
        ILogger<SignatureDescriptionService> logger,
        LlmDescriptionCoordinator coordinator,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _coordinator = coordinator;
        _signatureActivity = new();

        // Threshold for triggering description synthesis
        _requestThreshold = options.Value.SignatureDescriptionThreshold;

        _logger.LogInformation("SignatureDescriptionService initialized. Threshold: {Threshold} requests per signature",
            _requestThreshold);
    }

    /// <summary>
    ///     Track signature activity and potentially trigger description synthesis.
    /// </summary>
    public void TrackSignature(string signature, IReadOnlyDictionary<string, object?> signals)
    {
        if (string.IsNullOrEmpty(signature) || _requestThreshold <= 0)
            return;

        var shouldSynthesize = false;
        _signatureActivity.AddOrUpdate(signature,
            _ => new SignatureActivityTracker(signature, 1, new(signals)),
            (_, existing) =>
            {
                var updated = new SignatureActivityTracker(
                    signature,
                    existing.RequestCount + 1,
                    new(signals));

                // Check if we've hit the threshold - trigger description synthesis
                if (updated.RequestCount == _requestThreshold)
                {
                    _logger.LogInformation(
                        "Signature {Sig} reached threshold ({Count} requests), queuing description synthesis",
                        signature[..Math.Min(16, signature.Length)], _requestThreshold);
                    shouldSynthesize = true;
                }

                return updated;
            });

        // Enqueue to constrained coordinator (replaces raw Task.Run)
        if (shouldSynthesize)
        {
            _ = _coordinator.EnqueueSignatureAsync(signature, signals, CancellationToken.None);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Background cleanup loop - remove stale signatures periodically
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);

                // Clean up signatures that haven't been seen in a while
                var keysToRemove = _signatureActivity
                    .Where(kvp => kvp.Value.RequestCount < _requestThreshold)
                    .Select(kvp => kvp.Key)
                    .Take(1000) // Limit cleanup to 1000 per cycle
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _signatureActivity.TryRemove(key, out _);
                }

                if (keysToRemove.Count > 0)
                {
                    _logger.LogDebug("Cleaned {Count} inactive signatures from tracker", keysToRemove.Count);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in signature activity cleanup");
            }
        }
    }

    /// <summary>
    ///     Get current activity stats (for diagnostics).
    /// </summary>
    public (int Total, int ThresholdReached) GetActivityStats()
    {
        var total = _signatureActivity.Count;
        var reached = _signatureActivity.Values.Count(t => t.RequestCount >= _requestThreshold);
        return (total, reached);
    }

    public override void Dispose()
    {
        _signatureActivity.Clear();
        base.Dispose();
    }
}
