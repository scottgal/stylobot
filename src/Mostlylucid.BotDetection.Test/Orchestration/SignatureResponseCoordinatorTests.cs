using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public class SignatureResponseCoordinatorCacheTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        // Arrange
        var logger = NullLogger<SignatureResponseCoordinatorCache>.Instance;

        // Act
        var cache = new SignatureResponseCoordinatorCache(logger);

        // Assert
        Assert.NotNull(cache);
    }

    [Fact]
    public void Constructor_AcceptsCustomSettings()
    {
        // Arrange
        var logger = NullLogger<SignatureResponseCoordinatorCache>.Instance;

        // Act
        var cache = new SignatureResponseCoordinatorCache(
            logger,
            1000,
            TimeSpan.FromMinutes(15));

        // Assert
        Assert.NotNull(cache);
    }

    [Fact]
    public async Task GetOrCreateAsync_CreatesNewCoordinator()
    {
        // Arrange
        var logger = NullLogger<SignatureResponseCoordinatorCache>.Instance;
        var cache = new SignatureResponseCoordinatorCache(logger);

        // Act
        var coordinator = await cache.GetOrCreateAsync("test-sig");

        // Assert
        Assert.NotNull(coordinator);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReturnsSameCoordinatorForSameSignature()
    {
        // Arrange
        var logger = NullLogger<SignatureResponseCoordinatorCache>.Instance;
        var cache = new SignatureResponseCoordinatorCache(logger);

        // Act
        var coordinator1 = await cache.GetOrCreateAsync("test-sig");
        var coordinator2 = await cache.GetOrCreateAsync("test-sig");

        // Assert
        Assert.Same(coordinator1, coordinator2);
    }

    [Fact]
    public async Task GetOrCreateAsync_CreatesDifferentCoordinatorsForDifferentSignatures()
    {
        // Arrange
        var logger = NullLogger<SignatureResponseCoordinatorCache>.Instance;
        var cache = new SignatureResponseCoordinatorCache(logger);

        // Act
        var coordinator1 = await cache.GetOrCreateAsync("sig-1");
        var coordinator2 = await cache.GetOrCreateAsync("sig-2");

        // Assert
        Assert.NotSame(coordinator1, coordinator2);
    }

    [Fact]
    public async Task DisposeAsync_CleansUpResources()
    {
        // Arrange
        var logger = NullLogger<SignatureResponseCoordinatorCache>.Instance;
        var cache = new SignatureResponseCoordinatorCache(logger);
        await cache.GetOrCreateAsync("test-sig");

        // Act & Assert - should not throw
        await cache.DisposeAsync();
    }
}

public class SignatureResponseCoordinatorTests
{
    [Fact]
    public void Constructor_InitializesSuccessfully()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var sink = new SignalSink(100, TimeSpan.FromMinutes(5));

        // Act
        var coordinator = new SignatureResponseCoordinator("test-sig", logger, sink);

        // Assert
        Assert.NotNull(coordinator);
    }

    [Fact]
    public async Task DisposeAsync_CleansUpResources()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var coordinator = new SignatureResponseCoordinator("test-sig", logger, new SignalSink(100, TimeSpan.FromMinutes(5)));

        // Act & Assert - should not throw
        await coordinator.DisposeAsync();
    }
}

public class SignatureResponseCoordinatorSharedSinkTests
{
    [Fact]
    public async Task GetOrCreateAsync_TwoCoordinators_ShareTheSameSink()
    {
        var sharedSink = new SignalSink(100, TimeSpan.FromMinutes(5));
        var cache = new SignatureResponseCoordinatorCache(
            NullLogger<SignatureResponseCoordinatorCache>.Instance,
            sharedSink: sharedSink);

        var coord1 = await cache.GetOrCreateAsync("sig-aaa");
        var coord2 = await cache.GetOrCreateAsync("sig-bbb");

        Assert.Same(sharedSink, coord1.Sink);
        Assert.Same(sharedSink, coord2.Sink);
    }
}