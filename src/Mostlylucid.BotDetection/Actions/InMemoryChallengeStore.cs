using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Ephemeral in-memory PoW challenge store. Challenges and verification
///     results live in <see cref="ConcurrentDictionary"/>s for the process
///     lifetime; nothing persists across restarts. Expired challenges are
///     reaped opportunistically on each <see cref="CreateChallenge"/> call.
///
///     Used by <see cref="Extensions.ServiceCollectionExtensions.AddBotDetectionInMemory"/>;
///     challenges are inherently short-lived (typically minutes) so the
///     no-persistence trade-off is acceptable for the ephemeral deployment
///     shape this mode targets.
/// </summary>
public sealed class InMemoryChallengeStore : IChallengeStore
{
    private readonly ConcurrentDictionary<string, ChallengeRecord> _challenges = new();
    private readonly ConcurrentDictionary<string, ChallengeVerificationResult> _verifications = new();
    private long _opsSinceReap;
    private const long ReapInterval = 256;

    public ChallengeRecord CreateChallenge(string signature, int puzzleCount, int requiredZeros, TimeSpan expiry)
    {
        if (Interlocked.Increment(ref _opsSinceReap) % ReapInterval == 0)
            ReapExpired();

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var now = DateTimeOffset.UtcNow;
        var puzzles = new PuzzleSeed[puzzleCount];
        for (var i = 0; i < puzzleCount; i++)
            puzzles[i] = new PuzzleSeed(RandomNumberGenerator.GetBytes(16), requiredZeros);

        var record = new ChallengeRecord
        {
            Id = id,
            Signature = signature,
            PuzzleCount = puzzleCount,
            RequiredZeros = requiredZeros,
            Puzzles = puzzles,
            CreatedAt = now,
            ExpiresAt = now + expiry,
        };
        _challenges[id] = record;
        return record;
    }

    public ChallengeRecord? ValidateAndConsume(string challengeId)
    {
        if (!_challenges.TryRemove(challengeId, out var record)) return null;
        return record.ExpiresAt < DateTimeOffset.UtcNow ? null : record;
    }

    public void RecordVerification(ChallengeVerificationResult result)
        => _verifications[result.Signature] = result;

    public ChallengeVerificationResult? GetVerification(string signature)
        => _verifications.TryGetValue(signature, out var v) ? v : null;

    private void ReapExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _challenges)
            if (kv.Value.ExpiresAt < now)
                _challenges.TryRemove(kv.Key, out _);
    }
}
