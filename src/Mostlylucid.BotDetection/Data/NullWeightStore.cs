namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Ephemeral-mode no-op: every weight read returns 0 (no learned adjustment),
///     every write is dropped. Detectors fall back to their default YAML weights;
///     adaptive learning is effectively disabled for the process lifetime.
/// </summary>
public sealed class NullWeightStore : IWeightStore
{
    private static readonly IReadOnlyDictionary<string, double> _emptyWeights
        = new Dictionary<string, double>();

    public Task<double> GetWeightAsync(string signatureType, string signature, CancellationToken ct = default)
        => Task.FromResult(0.0);

    public Task<IReadOnlyDictionary<string, double>> GetWeightsAsync(
        string signatureType, IEnumerable<string> signatures, CancellationToken ct = default)
        => Task.FromResult(_emptyWeights);

    public Task UpdateWeightAsync(
        string signatureType, string signature, double weight, double confidence, int observationCount,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RecordObservationAsync(
        string signatureType, string signature, bool wasBot, double detectionConfidence,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<LearnedWeight>> GetAllWeightsAsync(string signatureType, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LearnedWeight>>(Array.Empty<LearnedWeight>());

    public Task<WeightStoreStats> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult(new WeightStoreStats());

    public Task DecayOldWeightsAsync(TimeSpan maxAge, double decayFactor, CancellationToken ct = default)
        => Task.CompletedTask;
}
