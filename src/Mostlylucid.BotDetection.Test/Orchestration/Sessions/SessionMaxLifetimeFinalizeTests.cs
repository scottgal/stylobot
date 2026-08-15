using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Analysis;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     The session MAX-LIFETIME chunk boundary (stream- 2026-08-15): the gap is the
///     PRIMARY finalize threshold (the distance since the last request — operator:
///     "It's the distance since the last request, that is the threshold"); the max
///     lifetime CHUNKS the continuous class ONLY — sessions that never gap (5-minute
///     pings, always-on clients — "even for continuous we can chunk them") would
///     otherwise accumulate forever in memory and never leave a trace. The chunk is
///     configurable (default 30 min — the same cadence as the gap).
/// </summary>
public sealed class SessionMaxLifetimeFinalizeTests
{
    private static SessionRequest MakeRequest(RequestState state, DateTimeOffset ts)
        => new(state, ts, "/", 200);

    [Fact]
    public async Task Continuous_session_chunks_at_the_max_lifetime()
    {
        // Continuous activity (no gaps — the gap boundary can never fire): the session
        // chunks at the max lifetime, and the in-memory epoch continues fresh.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(
            cache, NullLogger<SessionStore>.Instance,
            sessionGapThreshold: TimeSpan.FromHours(1),   // no gap boundary ever fires
            sessionMaxLifetime: TimeSpan.FromMinutes(10));
        var finalized = new List<string>();
        store.SessionFinalized += (snap, _) => finalized.Add(snap.Signature);

        var t0 = DateTimeOffset.UtcNow;
        // Activity every 30s — well inside the gap threshold — for 12 minutes: the
        // epoch's age crosses the 10-minute max lifetime at the 21st request.
        for (var i = 0; i < 25; i++)
        {
            await store.RecordRequestAsync("sig-continuous", MakeRequest(RequestState.PageView, t0.AddMinutes(i * 0.5)));
        }

        // The epoch whose age crossed the max lifetime chunked exactly once (the
        // boundary is retrogressive — the crossing request finalizes the previous epoch).
        finalized.Should().ContainSingle(s => s == "sig-continuous",
            "a session under continuous activity must chunk when its total life exceeds the max lifetime");

        // The in-memory session continued as a fresh epoch.
        await store.RecordRequestAsync("sig-continuous", MakeRequest(RequestState.PageView, t0.AddMinutes(12.5)));
        finalized.Should().HaveCount(1,
            "the fresh epoch continues without an immediate second chunk");
    }

    [Fact]
    public async Task Gap_boundary_still_finalizes_within_the_max_lifetime()
    {
        // The max-lifetime boundary must not disturb the gap semantics: an idle gap past
        // the threshold finalizes as before (the session's age is under the max).
        var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(
            cache, NullLogger<SessionStore>.Instance,
            sessionGapThreshold: TimeSpan.FromMinutes(30),
            sessionMaxLifetime: TimeSpan.FromHours(2));
        var finalized = new List<string>();
        store.SessionFinalized += (snap, _) => finalized.Add(snap.Signature);

        var t0 = DateTimeOffset.UtcNow;
        await store.RecordRequestAsync("sig-gap", MakeRequest(RequestState.PageView, t0));
        await store.RecordRequestAsync("sig-gap", MakeRequest(RequestState.PageView, t0.AddMinutes(5)));
        // A 45-minute gap: past the 30-minute threshold, within the 2-hour max lifetime.
        await store.RecordRequestAsync("sig-gap", MakeRequest(RequestState.PageView, t0.AddMinutes(50)));

        finalized.Should().ContainSingle(s => s == "sig-gap",
            "the gap boundary fires within the max lifetime, as before");
    }
}
