using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Test.Storage;

public sealed class StoreInitServiceTests
{
    [Fact]
    public async Task Requested_startup_cancellation_propagates_without_a_storage_failure_warning()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var logger = new FakeLogger<StoreInitService<TestStore>>();
        var service = new StoreInitService<TestStore>(new TestStore(Task.FromCanceled(cancellation.Token)), logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(cancellation.Token));
        Assert.DoesNotContain(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Storage_failure_is_logged_and_preserves_the_hot_only_fallback_contract()
    {
        var failure = new IOException("storage unavailable");
        var logger = new FakeLogger<StoreInitService<TestStore>>();
        var service = new StoreInitService<TestStore>(new TestStore(Task.FromException(failure)), logger);

        await service.StartAsync(CancellationToken.None);

        var warning = Assert.Single(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Warning);
        Assert.Same(failure, warning.Exception);
    }

    private sealed class TestStore(Task initialization) : IStoreInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => initialization;
    }
}
