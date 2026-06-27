using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mostlylucid.Atoms.Ephemeral;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Drift-triggered LFU picker for the signature LLM-naming path. Replaces the
///     push-then-Task.Run flow inside the legacy <see cref="SignatureDescriptionService"/>:
///     middleware still calls <see cref="TrackSignature"/> on every request to bump the
///     per-signature counter; the picker walks the in-memory tracker each tick and
///     surfaces signatures whose request count has crossed
///     <see cref="BotDetectionOptions.SignatureDescriptionThreshold"/>.
///     Reservation against <see cref="SignatureInFlightSet"/> happens before return so
///     two concurrent ticks (or two pickers on the same atom) can't double-fire on the
///     same signature.
/// </summary>
public sealed class DriftTriggeredSignaturePicker : IEphemeralPicker<SignaturePickItem>
{
    private readonly SignatureInFlightSet _inFlight;
    private readonly int _requestThreshold;

    // Activity tracker: signature -> (request count, latest signals snapshot).
    // Mirrors SignatureDescriptionService._signatureActivity verbatim — same store,
    // same threshold semantics, just walked pull-style on tick instead of pushed.
    private readonly ConcurrentDictionary<string, SignatureActivityTracker> _signatureActivity = new(StringComparer.Ordinal);

    public DriftTriggeredSignaturePicker(
        SignatureInFlightSet inFlight,
        IOptions<BotDetectionOptions> options)
    {
        _inFlight = inFlight;
        _requestThreshold = options.Value.SignatureDescriptionThreshold;
    }

    /// <summary>
    ///     Push entry point preserved for the middleware. Mirrors
    ///     <c>SignatureDescriptionService.TrackSignature</c> verbatim: bumps the
    ///     per-signature counter and refreshes the latest signals snapshot. The
    ///     threshold cross is no longer a synchronous enqueue — the picker observes
    ///     the cross on the next tick walk.
    /// </summary>
    public void TrackSignature(string signature, IReadOnlyDictionary<string, object?> signals)
    {
        if (string.IsNullOrEmpty(signature) || _requestThreshold <= 0)
            return;

        _signatureActivity.AddOrUpdate(
            signature,
            _ => new SignatureActivityTracker(signature, 1, new Dictionary<string, object?>(signals)),
            (_, existing) => new SignatureActivityTracker(
                signature,
                existing.RequestCount + 1,
                new Dictionary<string, object?>(signals)));
    }

    public IReadOnlyList<SignaturePickItem> Pick(int maxCount)
    {
        if (maxCount <= 0 || _requestThreshold <= 0 || _signatureActivity.IsEmpty)
            return Array.Empty<SignaturePickItem>();

        // Walk the in-memory activity tracker (LFU-equivalent — same store the
        // legacy SignatureDescriptionService kept). Surface every signature past
        // threshold, filter against the in-flight set, reserve before returning.
        var picks = new List<SignaturePickItem>(Math.Min(maxCount, 16));
        foreach (var kvp in _signatureActivity)
        {
            if (picks.Count >= maxCount) break;
            var tracker = kvp.Value;
            if (tracker.RequestCount < _requestThreshold) continue;
            if (!_inFlight.TryReserve(tracker.Signature)) continue;
            picks.Add(new SignaturePickItem(tracker.Signature, tracker.LatestSignals));
        }
        return picks;
    }

    /// <summary>Diagnostics — total tracked + already past threshold.</summary>
    public (int Total, int ThresholdReached) GetActivityStats()
    {
        var total = _signatureActivity.Count;
        var reached = _signatureActivity.Values.Count(t => t.RequestCount >= _requestThreshold);
        return (total, reached);
    }

    private sealed record SignatureActivityTracker(
        string Signature,
        int RequestCount,
        IReadOnlyDictionary<string, object?> LatestSignals);
}
