using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Pins the task-#65 reference-implementation pattern:
///     <see cref="BotListUpdateService"/> raises
///     <see cref="BotListUpdatedSignal"/> on the shared sink after every
///     successful refresh, and never on failure. Other list updaters (JA3
///     corpus, well-known bots, verified-bot registry, etc.) should mirror
///     this shape when they migrate off the parasite-store polling model.
/// </summary>
public class BotListUpdateSignalTests
{
    private static TypedSignalSink<BotListUpdatedSignal> NewSink()
    {
        var inner = new SignalSink(maxCapacity: 16, maxAge: TimeSpan.FromMinutes(5));
        return new TypedSignalSink<BotListUpdatedSignal>(inner);
    }

    private static BotListUpdateService NewService(
        IBotListDatabase database,
        TypedSignalSink<BotListUpdatedSignal>? updateSignals = null,
        int maxDownloadRetries = 1)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            EnableBackgroundUpdates = true,
            MaxDownloadRetries = maxDownloadRetries,
            ListDownloadTimeoutSeconds = 30,
        });
        return new BotListUpdateService(
            database,
            NullLogger<BotListUpdateService>.Instance,
            options,
            patternCache: null,
            metrics: null,
            scheduleCoordinator: null,
            updateSignals: updateSignals);
    }

    [Fact]
    public async Task Successful_update_raises_updated_signal()
    {
        var sink = NewSink();
        var received = new List<BotListUpdatedSignal>();
        sink.TypedSignalRaised += evt => received.Add(evt.Payload);

        var database = new SuccessfulDatabase();
        using var service = NewService(database, sink);

        await service.PerformUpdateWithRetriesAsync();

        received.Should().ContainSingle();
        received[0].RecoveredFromFailures.Should().Be(0,
            "clean first-attempt success reports zero recovered failures");
    }

    [Fact]
    public async Task Failed_update_does_not_raise_signal()
    {
        var sink = NewSink();
        var received = new List<BotListUpdatedSignal>();
        sink.TypedSignalRaised += evt => received.Add(evt.Payload);

        var database = new AlwaysFailingDatabase();
        using var service = NewService(database, sink, maxDownloadRetries: 2);

        await service.PerformUpdateWithRetriesAsync();

        received.Should().BeEmpty(
            "no signal must fire when the update never succeeds -- consumers " +
            "would react to a failed refresh as if it had landed");
    }

    [Fact]
    public async Task Recovery_after_prior_failures_reports_count()
    {
        var sink = NewSink();
        var received = new List<BotListUpdatedSignal>();
        sink.TypedSignalRaised += evt => received.Add(evt.Payload);

        var database = new FlakyDatabase(failuresBeforeSuccess: 2);
        using var service = NewService(database, sink, maxDownloadRetries: 5);

        await service.PerformUpdateWithRetriesAsync();

        received.Should().ContainSingle();
        received[0].RecoveredFromFailures.Should().Be(2,
            "the signal must carry the failure count so operators / monitoring can " +
            "distinguish clean successes from flaky-network refreshes");
    }

    [Fact]
    public async Task No_sink_configured_does_not_throw()
    {
        // Hosts that don't register the sink still get the update; the
        // notification is optional.
        var database = new SuccessfulDatabase();
        using var service = NewService(database, updateSignals: null);

        await service.PerformUpdateWithRetriesAsync();

        // No assertions beyond "no exception".
    }

    // ── Fake databases ────────────────────────────────────────────────

    private sealed class SuccessfulDatabase : NullBotListDatabase
    {
        public override Task UpdateListsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AlwaysFailingDatabase : NullBotListDatabase
    {
        public override Task UpdateListsAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("simulated network failure");
    }

    private sealed class FlakyDatabase : NullBotListDatabase
    {
        private int _remaining;
        public FlakyDatabase(int failuresBeforeSuccess) => _remaining = failuresBeforeSuccess;

        public override Task UpdateListsAsync(CancellationToken ct = default)
        {
            if (_remaining > 0)
            {
                _remaining--;
                throw new InvalidOperationException("simulated transient failure");
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Minimal <see cref="IBotListDatabase"/> stub. Override only what
    ///     the update path touches so a schema change to the interface
    ///     doesn't require every test to update.
    /// </summary>
    private class NullBotListDatabase : IBotListDatabase
    {
        public virtual Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task UpdateListsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task<bool> IsBot(string userAgent, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public virtual Task<BotInfo?> GetBotInfo(string userAgent, CancellationToken cancellationToken = default)
            => Task.FromResult<BotInfo?>(null);
        public virtual Task<bool> IsDatacenterIp(string ipAddress, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public virtual Task<DateTime?> GetLastUpdateTimeAsync(string listType, CancellationToken cancellationToken = default)
            => Task.FromResult<DateTime?>(null);
        public virtual Task<IReadOnlyList<string>> GetBotPatternsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public virtual Task<IReadOnlyList<string>> GetDatacenterIpRangesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
