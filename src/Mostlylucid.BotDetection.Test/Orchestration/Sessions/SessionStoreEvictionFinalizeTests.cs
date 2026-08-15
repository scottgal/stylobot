using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Analysis;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     The sessions ladder's precondition (stream- 2026-08-15): SessionFinalized must
///     fire when a session slot leaves the cache by TTL / capacity / removal — under
///     CONTINUOUS traffic (no gap boundaries ever) the eviction IS the session's natural
///     completion; without the finalize the session evaporates and the ladder's
///     session-row write (AddSessionAsync) never sees it.
///     <para>
///     NOTE: IMemoryCache invokes post-eviction callbacks on a DEFERRED background task,
///     not synchronously on the removing thread — the tests poll for the finalize.
///     </para>
/// </summary>
public sealed class SessionStoreEvictionFinalizeTests
{
    private static SessionRequest MakeRequest(RequestState state, DateTimeOffset ts)
        => new(state, ts, "/", 200);

    [Fact]
    public async Task Explicit_removal_finalizes_the_session()
    {
        // The shutdown flush removes each active slot; the removal is the session's
        // completion and must finalize exactly once (the flush delegates to the
        // Removed-eviction callback — a double finalize would write two rows).
        var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(cache, NullLogger<SessionStore>.Instance, sessionGapThreshold: TimeSpan.FromHours(1));
        var finalized = new List<string>();
        store.SessionFinalized += (snap, _) => finalized.Add(snap.Signature);

        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
            await store.RecordRequestAsync("sig-flush", MakeRequest(RequestState.PageView, t0.AddSeconds(i)));

        await store.FlushAllActiveSessionsAsync();

        await WaitForAsync(() => finalized.Count == 1);
        finalized.Should().ContainSingle(s => s == "sig-flush",
            "the flush's removal must finalize the session exactly once");
    }

    [Fact]
    public async Task Replaced_eviction_does_not_finalize_the_still_live_session()
    {
        // A fresh request for the same signature re-Sets the slot (Replaced reason) —
        // the session is still live; the finalize must only fire via the boundary or a
        // genuine eviction.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(cache, NullLogger<SessionStore>.Instance, sessionGapThreshold: TimeSpan.FromHours(1));
        var finalized = new List<string>();
        store.SessionFinalized += (snap, _) => finalized.Add(snap.Signature);

        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
            await store.RecordRequestAsync("sig-live", MakeRequest(RequestState.PageView, t0.AddSeconds(i)));
        // A fourth request within the gap: same session, no boundary, no finalize.
        await store.RecordRequestAsync("sig-live", MakeRequest(RequestState.ApiCall, t0.AddSeconds(3)));

        // Give the deferred eviction callbacks a window to fire — none must finalize.
        await Task.Delay(200);
        finalized.Should().BeEmpty("a still-live session must not finalize on re-set");
    }

    [Fact]
    public async Task Gap_boundary_still_finalizes_via_the_retrogressive_detection()
    {
        // The ladder must not break the existing boundary path: a request past the gap
        // threshold finalizes the previous session exactly once, and the new session
        // continues.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(cache, NullLogger<SessionStore>.Instance, sessionGapThreshold: TimeSpan.FromSeconds(5));
        var finalized = new List<string>();
        store.SessionFinalized += (snap, _) => finalized.Add(snap.Signature);

        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
            await store.RecordRequestAsync("sig-boundary", MakeRequest(RequestState.PageView, t0.AddSeconds(i)));
        // A request well past the 5s gap: the retrogressive boundary fires.
        await store.RecordRequestAsync("sig-boundary", MakeRequest(RequestState.ApiCall, t0.AddSeconds(30)));

        finalized.Should().ContainSingle(s => s == "sig-boundary",
            "the gap boundary must still finalize the previous session exactly once");
    }

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("WaitFor predicate did not become true within timeout.");
    }
}
