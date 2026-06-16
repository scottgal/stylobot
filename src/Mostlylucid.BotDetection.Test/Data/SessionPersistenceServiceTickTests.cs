using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Wave 2 Category B regression coverage for
///     <see cref="SessionPersistenceService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///     <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c> on a bounded
///     channel of <c>(SessionSnapshot, IReadOnlyList&lt;SessionRequest&gt;)</c>;
///     now subscribes to <see cref="TickCadence.Tick10s"/> and each tick
///     drains the channel non-blocking via
///     <see cref="System.Threading.Channels.ChannelReader{T}.TryRead"/>.
///     <para>
///         Four facts: subscription shape, tick runs against empty channel,
///         dispose releases the subscription + the SessionFinalized event
///         handler, and the tick drains the channel by routing each entry
///         through <see cref="ISessionStore.AddSessionAsync"/>.
///     </para>
///     <para>
///         Boot init + graceful-shutdown drain moved out to
///         <c>SessionPersistenceLifecycleHostedService</c> and are covered
///         by the lifecycle host's existing wiring; the unit tests here
///         exercise only the migrated singleton's tick contract.
///     </para>
/// </summary>
public sealed class SessionPersistenceServiceTickTests
{
    private static SessionStore NewSessionStore() => new(
        new MemoryCache(new MemoryCacheOptions()),
        NullLogger<SessionStore>.Instance);

    private static SessionPersistenceService NewService(
        RecordingScheduleCoordinator coordinator,
        out RecordingSessionStore store,
        out SessionStore sessionStore)
    {
        store = new RecordingSessionStore();
        sessionStore = NewSessionStore();
        return new SessionPersistenceService(
            store,
            sessionStore,
            NullLogger<SessionPersistenceService>.Instance,
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator, out _, out _);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("SessionPersistenceService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_against_empty_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewService(coordinator, out var store, out _);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
        sut.QueueDepth.Should().Be(0);
        store.AddedSessions.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator_and_event()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var sut = NewService(coordinator, out var store, out var sessionStore);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // After dispose, firing the event must not enqueue further work --
        // the service has unsubscribed from SessionFinalized in Dispose.
        FireSessionFinalized(sessionStore, NewSnapshot("sig-after-dispose"));
        sut.QueueDepth.Should().Be(0);

        // Double-dispose must be safe.
        sut.Dispose();
    }

    [Fact]
    public async Task OnTickAsync_drains_session_finalized_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewService(coordinator, out var store, out var sessionStore);

        // Fire the SessionFinalized event twice -- the migrated service's
        // event handler writes both entries to its internal channel. Without
        // a tick fire the entries sit in the channel waiting for the next
        // drain.
        FireSessionFinalized(sessionStore, NewSnapshot("sig-a"));
        FireSessionFinalized(sessionStore, NewSnapshot("sig-b"));
        sut.QueueDepth.Should().Be(2);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Cat-B load-bearing assertion: the channel has been drained by the
        // tick handler. Each snapshot was routed through AddSessionAsync.
        sut.QueueDepth.Should().Be(0);
        store.AddedSessions.Select(s => s.Signature).Should().BeEquivalentTo(["sig-a", "sig-b"]);
        // Side-effects on the same drain pass: signature upsert + entity
        // resolution fire per session too.
        store.UpsertedSignatures.Should().HaveCount(2);
        store.ResolvedEntities.Should().HaveCount(2);
    }

    private static void FireSessionFinalized(SessionStore source, SessionSnapshot snapshot)
    {
        // SessionStore.SessionFinalized is a public event; firing it via
        // reflection is awkward, so route the test through the public flush
        // path which fires the event for active sessions. We avoid that
        // complexity by invoking the underlying delegate directly via the
        // public event's invocation list -- the event field is a public
        // CLR-event so subscribers register via "+=" and the test fires the
        // delegate through a private helper that mirrors the production
        // path.
        //
        // Simpler approach: reflection on the backing delegate field.
        var field = typeof(SessionStore).GetField("SessionFinalized",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var del = (Action<SessionSnapshot, IReadOnlyList<SessionRequest>>?)field?.GetValue(source);
        del?.Invoke(snapshot, Array.Empty<SessionRequest>());
    }

    private static SessionSnapshot NewSnapshot(string signature) => new()
    {
        Signature = signature,
        StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
        EndedAt = DateTimeOffset.UtcNow,
        RequestCount = 5,
        Vector = new float[10],
        Maturity = 0.5f,
        DominantState = RequestState.PageView
    };

    /// <summary>
    ///     Minimal recording <see cref="ISessionStore"/>: captures
    ///     <see cref="AddSessionAsync"/>, <see cref="UpsertSignatureAsync"/>,
    ///     and <see cref="ResolveEntityAsync"/> calls so the drain test can
    ///     confirm each tick-drained entry was routed through the persistence
    ///     path. All other ISessionStore members are no-op stubs.
    /// </summary>
    private sealed class RecordingSessionStore : ISessionStore
    {
        public List<PersistedSession> AddedSessions { get; } = new();
        public List<PersistedSignature> UpsertedSignatures { get; } = new();
        public List<string> ResolvedEntities { get; } = new();

        public string? PersistenceConnectionString => null;

        public Task<long> AddSessionAsync(PersistedSession session, CancellationToken ct = default)
        {
            AddedSessions.Add(session);
            return Task.FromResult((long)AddedSessions.Count);
        }

        public Task UpsertSignatureAsync(PersistedSignature signature, CancellationToken ct = default)
        {
            UpsertedSignatures.Add(signature);
            return Task.CompletedTask;
        }

        public Task<string> ResolveEntityAsync(string primarySignature, CancellationToken ct = default)
        {
            ResolvedEntities.Add(primarySignature);
            return Task.FromResult($"entity:{primarySignature}");
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        // No-op stubs for the rest of the interface -- the migrated service
        // touches only the three methods above on the hot tick-drain path.
        public Task IncrementBucketAsync(DateTime bucketTime, bool isBot, double processingTimeMs, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRequestAsync(PersistedRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRequestBatchAsync(IReadOnlyList<PersistedRequest> requests, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<PersistedRequest>> GetUnatomizedRequestsAsync(int limit = 5000, CancellationToken ct = default) => Task.FromResult(new List<PersistedRequest>());
        public Task<List<PersistedRequest>> GetRecentRequestsAsync(int limit = 5000, DateTime? sinceUtc = null, CancellationToken ct = default) => Task.FromResult(new List<PersistedRequest>());
        public Task LinkRequestsToSessionAsync(long sessionId, IReadOnlyList<long> requestIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<PersistedSession>> GetSessionsAsync(string signature, int limit = 20, CancellationToken ct = default) => Task.FromResult(new List<PersistedSession>());
        public Task<List<PersistedSession>> GetRecentSessionsAsync(int limit = 50, bool? isBot = null, DateTime? since = null, CancellationToken ct = default) => Task.FromResult(new List<PersistedSession>());
        public Task<PersistedSignature?> GetSignatureAsync(string signatureId, CancellationToken ct = default) => Task.FromResult<PersistedSignature?>(null);
        public Task<string> ResolveSignatureAsync(string requestedSignatureId, CancellationToken ct = default) => Task.FromResult(requestedSignatureId);
        public Task RecordSignatureMergeAsync(string oldSignatureId, string newSignatureId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<PersistedSignature>> GetTopSignaturesAsync(int limit = 20, bool? isBot = null, CancellationToken ct = default) => Task.FromResult(new List<PersistedSignature>());
        public Task<DashboardSessionSummary> GetSummaryAsync(CancellationToken ct = default) => Task.FromResult(new DashboardSessionSummary());
        public Task<List<AggregatedBucket>> GetTimeSeriesAsync(DateTime start, DateTime end, CancellationToken ct = default) => Task.FromResult(new List<AggregatedBucket>());
        public Task<List<CountrySessionStats>> GetCountryStatsAsync(int limit = 20, CancellationToken ct = default) => Task.FromResult(new List<CountrySessionStats>());
        public Task<List<(PersistedSession Session, float Similarity)>> FindSimilarSessionsAsync(float[] queryVector, int topK = 10, float minSimilarity = 0.7f, CancellationToken ct = default) => Task.FromResult(new List<(PersistedSession, float)>());
        public Task<ResolvedEntity?> GetEntityForSignatureAsync(string primarySignature, CancellationToken ct = default) => Task.FromResult<ResolvedEntity?>(null);
        public Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default) => Task.FromResult<ResolvedEntity?>(null);
        public Task<List<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default) => Task.FromResult(new List<EntityEdge>());
        public Task MergeSignatureAsync(string entityId, string signature, double confidence, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateEntityAsync(ResolvedEntity entity, CancellationToken ct = default) => Task.CompletedTask;
        public Task PruneAsync(TimeSpan retention, CancellationToken ct = default) => Task.CompletedTask;
        public Task PruneBucketsAsync(TimeSpan retention, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<(string Signature, int SessionCount)>> GetOverflowingSignaturesAsync(int maxPerSignature, int limit = 500, CancellationToken ct = default) => Task.FromResult(new List<(string, int)>());
        public Task<CompactionResult> CompactSignatureSessionsAsync(string signature, int keepCount, CancellationToken ct = default) => Task.FromResult(new CompactionResult { Signature = signature });
        public Task<List<CompactionSignatureInfo>> GetSignaturePriorityInfoAsync(List<string> signatures, CancellationToken ct = default) => Task.FromResult(new List<CompactionSignatureInfo>());
        public Task<List<string>> GetActiveEntityIdsAsync(DateTime cutoff, int limit = 100, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}