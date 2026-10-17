using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Test.Storage;

public sealed class WriteBehindLfuReliabilityTests
{
    [Fact]
    public async Task Durable_read_failure_is_visible_without_keys_or_a_warning_per_request()
    {
        var logger = new FakeLogger();
        using var store = new TestStore(logger) { LoadFailure = new IOException("database unavailable") };

        Assert.Null(await store.GetAsync("private-request-key"));
        Assert.Null(await store.GetAsync("another-private-key"));
        var warning = Assert.Single(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Warning);
        Assert.IsType<IOException>(warning.Exception);
        Assert.Contains("durable read failed", warning.Message);
        Assert.DoesNotContain("private", warning.Message);

        store.LoadFailure = null;
        Assert.Null(await store.GetAsync("missing-row"));
        Assert.Contains(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Information && record.Message.Contains("recovered"));

        store.LoadFailure = new IOException("database unavailable again");
        await store.GetAsync("private-request-key");
        Assert.Equal(2, logger.Collector.GetSnapshot().Count(record => record.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task Requested_read_cancellation_propagates_without_a_failure_warning()
    {
        var logger = new FakeLogger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var store = new TestStore(logger) { LoadFailure = new OperationCanceledException(cancellation.Token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.GetAsync("key", cancellation.Token));
        Assert.DoesNotContain(logger.Collector.GetSnapshot(), record => record.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_worker_failure_is_logged_and_persistence_recovers(bool sampleDrain)
    {
        var logger = new FakeLogger();
        var capReads = 0;
        int Cap() => Interlocked.Increment(ref capReads) == 1
            ? throw new InvalidOperationException("injected cap-provider failure")
            : 1000;
        using TestStore store = sampleDrain ? new SampleStore(logger, Cap) : new TestStore(logger, Cap);
        store.Record("key", new Entry(1));

        await store.Persisted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = Assert.Single(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains("restarting", error.Message);
        Assert.True(Volatile.Read(ref capReads) >= 2);
    }

    private sealed record Entry(int Value);

    private class TestStore : WriteBehindLfuStore<string, Entry, Entry>
    {
        public Exception? LoadFailure { get; set; }
        public TaskCompletionSource Persisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestStore(ILogger logger, Func<int>? cap = null)
            : base(1000, 1000, 10, TimeSpan.FromMilliseconds(10), logger, maxEntriesProvider: cap) { }

        protected override Entry CreateInitial(string key, Entry op) => op;
        protected override Entry MergeIntoExisting(string key, Entry existing, Entry op) => op;
        protected override ValueTask<Entry?> LoadFromDurableTierAsync(string key, CancellationToken ct) =>
            LoadFailure is { } error ? ValueTask.FromException<Entry?>(error) : ValueTask.FromResult<Entry?>(null);
        protected override Task PersistBatchAsync(IReadOnlyList<Entry> batch, CancellationToken ct)
        {
            Persisted.TrySetResult();
            return Task.CompletedTask;
        }
        protected override Task PersistValuesBatchAsync(IReadOnlyList<KeyValuePair<string, Entry>> batch, CancellationToken ct)
        {
            Persisted.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class SampleStore(ILogger logger, Func<int> cap) : TestStore(logger, cap)
    {
        protected override bool UseBehaviouralSampleDrain => true;
    }
}
