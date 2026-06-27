using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Shared per-key sequentiality guard for the signature LLM path. Replaces
///     the KeyedSequentialAtom that the queue-based LlmDescriptionCoordinator
///     used. Picker reserves keys before returning items; writeback releases
///     them after persistence. Invoker failures (where the EphemeralLlmCoordinator
///     skips writeback entirely) are reclaimed automatically by the
///     <see cref="_staleAfter"/> window — once a reservation is older than that
///     window <see cref="TryReserve"/> will refresh + return true. Default 60s,
///     comfortably longer than the coordinator's default 30s invocation timeout.
/// </summary>
public sealed class SignatureInFlightSet
{
    private readonly ConcurrentDictionary<string, DateTime> _set = new(StringComparer.Ordinal);
    private readonly TimeSpan _staleAfter;

    public SignatureInFlightSet(TimeSpan? staleAfter = null)
        => _staleAfter = staleAfter ?? TimeSpan.FromSeconds(60);

    public bool TryReserve(string key)
    {
        var now = DateTime.UtcNow;
        while (true)
        {
            if (_set.TryAdd(key, now)) return true;
            if (!_set.TryGetValue(key, out var existing)) continue;
            if (now - existing < _staleAfter) return false;
            if (_set.TryUpdate(key, now, existing)) return true;
        }
    }

    public void Release(string key) => _set.TryRemove(key, out _);

    public int Count => _set.Count;
}
