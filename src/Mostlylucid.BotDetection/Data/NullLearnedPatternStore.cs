using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Ephemeral-mode no-op: the learning system can still observe patterns
///     in-memory within a process lifetime via its own contributor caches, but
///     nothing is persisted -- learned patterns evaporate on restart.
/// </summary>
public sealed class NullLearnedPatternStore : ILearnedPatternStore
{
    public Task UpsertAsync(LearnedSignature signature, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<LearnedSignature>> GetByTypeAsync(string signatureType, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LearnedSignature>>(Array.Empty<LearnedSignature>());

    public Task<IReadOnlyList<LearnedSignature>> GetByConfidenceAsync(double minConfidence, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LearnedSignature>>(Array.Empty<LearnedSignature>());

    public Task<LearnedSignature?> GetAsync(string patternId, CancellationToken ct = default)
        => Task.FromResult<LearnedSignature?>(null);

    public Task DeleteAsync(string patternId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<LearnedSignature>> GetPendingFeedbackAsync(int minOccurrences, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LearnedSignature>>(Array.Empty<LearnedSignature>());

    public Task MarkFedBackAsync(string patternId, CancellationToken ct = default) => Task.CompletedTask;

    public Task CleanupOldPatternsAsync(TimeSpan maxAge, CancellationToken ct = default) => Task.CompletedTask;

    public Task<PatternStoreStats> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult(new PatternStoreStats());
}
