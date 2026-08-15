using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration.Sessions;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     The finalize set PORTED into the signals-native Orchestration
///     <see cref="SessionStore"/> (stream- ruling 2026-08-15 — the production session
///     path): the max-lifetime CHUNK (the never-idle class — the operator's "even for
///     continuous we can chunk them") fires at the upsert boundary when the session's
///     age from creation exceeds <see cref="SessionStoreOptions.MaxLifetime"/>, and
///     the IDLE SWEEP (the stopped class — "the distance since the last request, that
///     is the threshold") finalizes sessions whose last contribution is past
///     <see cref="SessionStoreOptions.MaxIdle"/>. Both finalizes are delegated to the
///     bounded cache's onEvict — the single finalize owner (the Lifecycle chain); a
///     synchronous finalize plus the invalidate would double-fire.
/// </summary>
public sealed class SessionStoreOrchestrationFinalizeTests
{
    private static SessionSample MakeSample(string siteId, string fingerprintId, DateTimeOffset ts) => new()
    {
        SiteId = siteId,
        FingerprintId = fingerprintId,
        Timestamp = ts,
        BotProbability = 0.3,
        Confidence = 0.7,
        StatusCode = 200,
        FromUpstream = true,
        Honeypot = false,
    };

    [Fact]
    public async Task UpsertAsync_chunks_the_epoch_at_MaxLifetime()
    {
        // deploy-'s positive-control shape: burst → idle → triggering request — the
        // chunk must fire (the Lifecycle finalize chain) and the epoch restarts.
        var options = Options.Create(new SessionStoreOptions
        {
            MaxLifetime = TimeSpan.FromSeconds(10),
            // The rig's discriminator knob surface: TTL long enough that only the
            // chunk (not the cache's sliding TTL) can fire within the test window.
            Ttl = TimeSpan.FromMinutes(30),
        });
        var store = new SessionStore(options, NullLogger<SessionStore>.Instance);
        var finalized = new List<SessionFinalizingSignal>();
        store.Lifecycle.TypedSignalRaised += evt => finalized.Add(evt.Payload);

        var t0 = DateTimeOffset.UtcNow;
        // Burst (continuous activity — no gaps).
        await store.UpsertAsync(MakeSample("site", "fp", t0), CancellationToken.None);
        await store.UpsertAsync(MakeSample("site", "fp", t0.AddSeconds(1)), CancellationToken.None);
        await store.UpsertAsync(MakeSample("site", "fp", t0.AddSeconds(2)), CancellationToken.None);

        // Triggering request past the 10s lifetime: the chunk finalizes the epoch.
        await store.UpsertAsync(MakeSample("site", "fp", t0.AddSeconds(30)), CancellationToken.None);

        await WaitForAsync(() => finalized.Count == 1);
        finalized[0].FingerprintId.Should().Be("fp");
        finalized[0].SiteId.Should().Be("site");
        finalized[0].Aggregate.Should().NotBeNull("the finalize carries the epoch's projection");

        // The epoch restarted: the triggering request continues in the fresh epoch.
        var agg = await store.UpsertAsync(MakeSample("site", "fp", t0.AddSeconds(31)), CancellationToken.None);
        agg.SampleCount.Should().BeLessThanOrEqualTo(2,
            "the chunk restarted the epoch — the fresh session starts counting again");
    }

    [Fact]
    public async Task Idle_sweep_finalizes_sessions_past_MaxIdle_and_skips_active_ones()
    {
        // The stopped/paused class: sessions whose LAST contribution is older than
        // MaxIdle finalize via the sweep, independently of request arrival. A
        // recently-active session is untouched.
        var options = Options.Create(new SessionStoreOptions { MaxIdle = TimeSpan.FromSeconds(5) });
        var store = new SessionStore(options, NullLogger<SessionStore>.Instance);
        var finalized = new List<SessionFinalizingSignal>();
        store.Lifecycle.TypedSignalRaised += evt => finalized.Add(evt.Payload);

        var t0 = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakeSample("site", "fp-active", t0), CancellationToken.None);
        await store.UpsertAsync(MakeSample("site", "fp-idle", t0.AddSeconds(-30)), CancellationToken.None);

        var removed = await store.FinalizeIdleSessionsAsync(t0, CancellationToken.None);
        removed.Should().Be(1, "only the idle session is removed by the sweep");

        await WaitForAsync(() => finalized.Count == 1);
        finalized[0].FingerprintId.Should().Be("fp-idle",
            "the idle session's finalize lands via the cache's onEvict Lifecycle chain");
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
