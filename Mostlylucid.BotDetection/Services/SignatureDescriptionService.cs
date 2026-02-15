using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that generates LLM-based descriptions for signatures once they reach a request threshold.
///     Monitors signature activity and triggers description synthesis when request count exceeds the configured limit.
/// </summary>
public class SignatureDescriptionService : BackgroundService
{
    private readonly ILogger<SignatureDescriptionService> _logger;
    private readonly IBotNameSynthesizer _synthesizer;
    private readonly int _requestThreshold;
    private readonly ConcurrentDictionary<string, SignatureActivityTracker> _signatureActivity;

    public event EventHandler<(string Signature, string? Name, string? Description)>? DescriptionGenerated;

    private record SignatureActivityTracker(string Signature, int RequestCount, Dictionary<string, object?> LatestSignals);

    public SignatureDescriptionService(
        ILogger<SignatureDescriptionService> logger,
        IBotNameSynthesizer synthesizer,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _synthesizer = synthesizer;
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

        // Fire async synthesis outside the update operation
        if (shouldSynthesize)
        {
            _ = Task.Run(() => SynthesizeDescriptionAsync(signature, signals));
        }
    }

    /// <summary>
    ///     Generate description for a signature when threshold is reached.
    /// </summary>
    private async Task SynthesizeDescriptionAsync(
        string signature,
        IReadOnlyDictionary<string, object?> signals,
        CancellationToken ct = default)
    {
        try
        {
            if (!_synthesizer.IsReady)
            {
                _logger.LogDebug("Synthesizer not ready, skipping description for signature {Sig}",
                    signature[..Math.Min(16, signature.Length)]);
                return;
            }

            var (name, description) = await _synthesizer.SynthesizeDetailedAsync(signals, ct: ct);

            if (!string.IsNullOrEmpty(name))
            {
                _logger.LogInformation(
                    "Generated description for signature {Sig}: '{Name}'",
                    signature[..Math.Min(16, signature.Length)], name);

                DescriptionGenerated?.Invoke(this, (signature, name, description));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate description for signature {Sig}",
                signature[..Math.Min(16, signature.Length)]);
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
