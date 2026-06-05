using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     In-process LRU-by-insertion-order cache of encoded identity vectors, keyed on
///     <c>primary_signature</c>. Solves the sub-resource amortisation gap: every request
///     in the foundation wave runs <see cref="IdentityVectorEncoder.Encode"/>, but the
///     identity-stable portion of the vector (network, locale, header bag, transport,
///     UA family) is invariant across all requests from the same client within a session.
///     A page load that pulls 30 sub-resources shouldn't pay the ~1 μs encode cost
///     thirty times.
///
///     Cache discipline:
///     <list type="bullet">
///         <item>Lookups are wait-free (<see cref="ConcurrentDictionary{TKey,TValue}.TryGetValue"/>).</item>
///         <item>Entries expire by absolute TTL (<see cref="IdentityVectorOptions.EncoderCacheTtlMs"/>).
///               The default of 1 second covers a typical page-load asset burst without letting
///               the cached vector go stale on volatile session.* dims (request_rate, path_entropy,
///               session_age) for long enough to matter for downstream matching.</item>
///         <item>Eviction is opportunistic on <see cref="Set"/> when entry count exceeds
///               the configured maximum. Drops oldest by insertion timestamp.</item>
///         <item>Entries are immutable: the encoder returns a fresh <c>float[]</c> per call,
///               and the cache hands back the same reference. Callers MUST NOT mutate the
///               returned array.</item>
///     </list>
/// </summary>
public sealed class EncoderResultCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries
        = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly long _ttlTicks;
    private readonly bool _enabled;

    public EncoderResultCache(IOptions<BotDetectionOptions> options)
    {
        var vec = options.Value.Identity.Vector;
        _enabled = options.Value.Identity.Enabled && vec.EncoderCacheEnabled;
        _maxEntries = Math.Max(1, vec.EncoderCacheMaxEntries);
        _ttlTicks = TimeSpan.FromMilliseconds(Math.Max(1, vec.EncoderCacheTtlMs)).Ticks;
    }

    /// <summary>
    ///     Returns true when <paramref name="primarySignature"/> has a live cached vector
    ///     within the TTL window. The returned array is shared — DO NOT mutate it.
    /// </summary>
    public bool TryGet(string primarySignature, out float[]? vector)
    {
        if (!_enabled || string.IsNullOrEmpty(primarySignature))
        {
            vector = null;
            return false;
        }

        if (_entries.TryGetValue(primarySignature, out var entry))
        {
            if (DateTime.UtcNow.Ticks - entry.InsertedAtTicks < _ttlTicks)
            {
                vector = entry.Vector;
                return true;
            }
            // Expired; opportunistically remove so the slot is reclaimed.
            _entries.TryRemove(primarySignature, out _);
        }

        vector = null;
        return false;
    }

    /// <summary>
    ///     Insert / overwrite the vector for <paramref name="primarySignature"/>. Triggers
    ///     opportunistic eviction when the cache exceeds <c>EncoderCacheMaxEntries</c>.
    ///     Drops the entries with the oldest <c>InsertedAtTicks</c> first; sweeps a 10%
    ///     slice so eviction amortises across many Set calls instead of stalling one.
    /// </summary>
    public void Set(string primarySignature, float[] vector)
    {
        if (!_enabled || string.IsNullOrEmpty(primarySignature)) return;

        _entries[primarySignature] = new CacheEntry(vector, DateTime.UtcNow.Ticks);

        if (_entries.Count > _maxEntries)
            EvictOldest();
    }

    /// <summary>
    ///     Test-only / operator-tool clear. Not on the hot path.
    /// </summary>
    public void Clear() => _entries.Clear();

    private void EvictOldest()
    {
        // Sort a snapshot by InsertedAtTicks and drop the oldest 10%. Avoids
        // repeated single-entry eviction under bursty Set traffic.
        var dropTarget = _maxEntries / 10;
        if (dropTarget <= 0) dropTarget = 1;

        var snapshot = _entries.ToArray();
        if (snapshot.Length <= _maxEntries) return;

        Array.Sort(snapshot, static (a, b) => a.Value.InsertedAtTicks.CompareTo(b.Value.InsertedAtTicks));
        for (var i = 0; i < dropTarget && i < snapshot.Length; i++)
            _entries.TryRemove(snapshot[i].Key, out _);
    }

    private readonly record struct CacheEntry(float[] Vector, long InsertedAtTicks);
}