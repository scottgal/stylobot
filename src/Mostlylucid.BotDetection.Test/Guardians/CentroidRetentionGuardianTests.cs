using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Guardians;

/// <summary>
///     Behaviour-preservation tests for <see cref="CentroidRetentionGuardian"/>
///     (Phase 4 of the old VectorCompactionService). The guardian prunes stale
///     rows from all three centroid stores using the configured
///     <see cref="SelfMaintenanceOptions.CentroidRetentionDays"/> window.
///
///     Contract:
///     <list type="bullet">
///         <item>All three centroid stores receive a <c>Prune*OlderThanAsync</c>
///             call with a cutoff derived from <c>CentroidRetentionDays</c>.</item>
///         <item>The cutoff epoch is within a 2-second window of the expected
///             value so clock drift in CI cannot cause spurious failures.</item>
///         <item>The guardian returns <c>Status = "pruned"</c> when all three
///             prune calls succeeded and <c>"error"</c> when a store throws.</item>
///         <item>The guardian honours the <see cref="IGuardian"/> contract:
///             name, category, interval, and enabled flag.</item>
///         <item>Cancellation propagates; the guardian does not swallow it.</item>
///     </list>
/// </summary>
public sealed class CentroidRetentionGuardianTests
{
    // -----------------------------------------------------------------------
    // Tracking fakes (ported from VectorCompactionServiceTests)
    // -----------------------------------------------------------------------

    private sealed class TrackingSignatureStore : ISignatureCentroidStore
    {
        public bool PruneCalled { get; private set; }
        public long? ReceivedCutoff { get; private set; }

        public Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
        {
            PruneCalled = true;
            ReceivedCutoff = cutoffEpochSeconds;
            return Task.CompletedTask;
        }

        public Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SignatureCentroidRow>>(Array.Empty<SignatureCentroidRow>());
    }

    private sealed class TrackingSessionCentroidStore : ISessionCentroidStore
    {
        public bool PruneCalled { get; private set; }
        public long? ReceivedCutoff { get; private set; }

        public Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
        {
            PruneCalled = true;
            ReceivedCutoff = cutoffEpochSeconds;
            return Task.CompletedTask;
        }

        public Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCentroidRow>>(Array.Empty<SessionCentroidRow>());
    }

    private sealed class TrackingIntentStore : IIntentCentroidStore
    {
        public bool PruneCalled { get; private set; }
        public long? ReceivedCutoff { get; private set; }

        public Task PruneIntentsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
        {
            PruneCalled = true;
            ReceivedCutoff = cutoffEpochSeconds;
            return Task.CompletedTask;
        }

        public Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IntentCentroidRow>>(Array.Empty<IntentCentroidRow>());
    }

    private sealed class ThrowingSignatureStore : ISignatureCentroidStore
    {
        public Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("db locked"));

        public Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SignatureCentroidRow>>(Array.Empty<SignatureCentroidRow>());
    }

    private sealed class CancellingSignatureStore : ISignatureCentroidStore
    {
        public Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.FromException(new OperationCanceledException());

        public Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SignatureCentroidRow>>(Array.Empty<SignatureCentroidRow>());
    }

    // -----------------------------------------------------------------------
    // Helper: build CentroidRetentionGuardian with the given stores
    // -----------------------------------------------------------------------

    private static CentroidRetentionGuardian Build(
        ISignatureCentroidStore? sigStore = null,
        ISessionCentroidStore? sessStore = null,
        IIntentCentroidStore? intentStore = null,
        int centroidRetentionDays = 30,
        IConfiguration? config = null)
    {
        var options = new BotDetectionOptions
        {
            SelfMaintenance = new SelfMaintenanceOptions
            {
                CentroidRetentionDays = centroidRetentionDays
            }
        };

        return new CentroidRetentionGuardian(
            sigStore   ?? new TrackingSignatureStore(),
            sessStore  ?? new TrackingSessionCentroidStore(),
            intentStore ?? new TrackingIntentStore(),
            Options.Create(options),
            config ?? new ConfigurationBuilder().Build(),
            NullLogger<CentroidRetentionGuardian>.Instance);
    }

    // -----------------------------------------------------------------------
    // IGuardian contract
    // -----------------------------------------------------------------------

    [Fact]
    public void Is_a_data_category_guardian_named_CentroidRetention()
    {
        var sut = Build();

        sut.Should().BeAssignableTo<IGuardian>();
        sut.Name.Should().Be("CentroidRetention");
        sut.Category.Should().Be(GuardianCategory.Data);
    }

    [Fact]
    public void Interval_defaults_to_CompactionInterval_from_options()
    {
        var options = new BotDetectionOptions();
        options.Retention.CompactionInterval = TimeSpan.FromMinutes(45);

        var sut = new CentroidRetentionGuardian(
            new TrackingSignatureStore(),
            new TrackingSessionCentroidStore(),
            new TrackingIntentStore(),
            Options.Create(options),
            new ConfigurationBuilder().Build(),
            NullLogger<CentroidRetentionGuardian>.Instance);

        sut.Interval.Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void Interval_can_be_overridden_via_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:CentroidRetention:Interval"] = "06:00:00"
            })
            .Build();

        var sut = Build(config: config);

        sut.Interval.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public void Enabled_defaults_to_true()
    {
        var sut = Build();
        sut.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Enabled_can_be_set_false_via_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:CentroidRetention:Enabled"] = "false"
            })
            .Build();

        var sut = Build(config: config);
        sut.Enabled.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // GuardAsync: prune contract (ported from VectorCompactionServiceTests)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GuardAsync_calls_prune_on_all_three_stores()
    {
        const int retentionDays = 30;
        var sigStore    = new TrackingSignatureStore();
        var sessStore   = new TrackingSessionCentroidStore();
        var intentStore = new TrackingIntentStore();

        var sut = Build(sigStore, sessStore, intentStore, retentionDays);

        var beforeCall = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeSeconds();

        var report = await sut.GuardAsync();

        var afterCall = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeSeconds();

        sigStore.PruneCalled.Should().BeTrue("ISignatureCentroidStore.PruneSignaturesOlderThanAsync was not called");
        sessStore.PruneCalled.Should().BeTrue("ISessionCentroidStore.PruneSessionsOlderThanAsync was not called");
        intentStore.PruneCalled.Should().BeTrue("IIntentCentroidStore.PruneIntentsOlderThanAsync was not called");

        sigStore.ReceivedCutoff.Should().BeInRange(beforeCall, afterCall + 2,
            "cutoff must be within a 2-second window of the expected epoch");
        sessStore.ReceivedCutoff.Should().BeInRange(beforeCall, afterCall + 2);
        intentStore.ReceivedCutoff.Should().BeInRange(beforeCall, afterCall + 2);

        report.GuardianName.Should().Be("CentroidRetention");
        report.Category.Should().Be(GuardianCategory.Data);
        report.Status.Should().Be("pruned");
        report.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GuardAsync_cutoff_uses_the_configured_retention_days()
    {
        const int retentionDays = 7;
        var sigStore = new TrackingSignatureStore();

        var sut = Build(sigStore, centroidRetentionDays: retentionDays);

        var before = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeSeconds();
        await sut.GuardAsync();
        var after  = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeSeconds();

        sigStore.ReceivedCutoff.Should().BeInRange(before, after + 2,
            "cutoff epoch must reflect the configured CentroidRetentionDays");
    }

    [Fact]
    public async Task GuardAsync_returns_error_status_when_a_store_throws()
    {
        var sut = Build(sigStore: new ThrowingSignatureStore());

        var report = await sut.GuardAsync();

        report.Status.Should().Be("error");
        report.Details.Should().Contain("db locked");
        report.GuardianName.Should().Be("CentroidRetention");
    }

    [Fact]
    public async Task GuardAsync_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var sut = Build(sigStore: new CancellingSignatureStore());

        Func<Task> act = () => sut.GuardAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
