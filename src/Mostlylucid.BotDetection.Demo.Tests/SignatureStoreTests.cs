using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using Mostlylucid.BotDetection.Demo.Services;
using Mostlylucid.BotDetection.Orchestration;
using Xunit;

namespace Mostlylucid.BotDetection.Demo.Tests;

public class SignatureStoreTests
{
    [Fact]
    public void StoreSignature_ShouldStoreSignatureSuccessfully()
    {
        // Arrange
        var store = new SignatureStore(TestHelpers.CreateMockLogger<SignatureStore>().Object, maxSignatures: 100);
        var evidence = CreateTestEvidence(botProbability: 0.85);
        var httpContext = CreateTestHttpContext();

        // Act
        store.StoreSignature("test-sig-123", evidence, httpContext);

        // Assert
        var retrieved = store.GetSignature("test-sig-123");
        retrieved.Should().NotBeNull();
        retrieved!.SignatureId.Should().Be("test-sig-123");
        retrieved.Evidence.BotProbability.Should().Be(0.85);
    }

    [Fact]
    public void StoreSignature_ShouldEvictOldestWhenFull()
    {
        // Arrange
        var store = new SignatureStore(TestHelpers.CreateMockLogger<SignatureStore>().Object, maxSignatures: 3);
        var evidence1 = CreateTestEvidence(0.5);
        var evidence2 = CreateTestEvidence(0.6);
        var evidence3 = CreateTestEvidence(0.7);
        var evidence4 = CreateTestEvidence(0.8);
        var httpContext = CreateTestHttpContext();

        // Act - Fill the store
        store.StoreSignature("sig-1", evidence1, httpContext);
        Thread.Sleep(10); // Ensure different timestamps
        store.StoreSignature("sig-2", evidence2, httpContext);
        Thread.Sleep(10);
        store.StoreSignature("sig-3", evidence3, httpContext);
        Thread.Sleep(10);

        // Add fourth signature - should evict sig-1
        store.StoreSignature("sig-4", evidence4, httpContext);

        // Assert
        store.GetSignature("sig-1").Should().BeNull("oldest signature should be evicted");
        store.GetSignature("sig-2").Should().NotBeNull();
        store.GetSignature("sig-3").Should().NotBeNull();
        store.GetSignature("sig-4").Should().NotBeNull();
    }

    [Fact]
    public void GetSignature_ShouldReturnNullForNonexistentSignature()
    {
        // Arrange
        var store = new SignatureStore(TestHelpers.CreateMockLogger<SignatureStore>().Object);

        // Act
        var result = store.GetSignature("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetRecentSignatures_ShouldReturnMostRecentFirst()
    {
        // Arrange
        var store = new SignatureStore(TestHelpers.CreateMockLogger<SignatureStore>().Object);
        var httpContext = CreateTestHttpContext();

        for (int i = 1; i <= 5; i++)
        {
            var evidence = CreateTestEvidence(i * 0.1);
            store.StoreSignature($"sig-{i}", evidence, httpContext);
            Thread.Sleep(10); // Ensure different timestamps
        }

        // Act
        var recent = store.GetRecentSignatures(count: 3);

        // Assert
        recent.Should().HaveCount(3);
        recent[0].SignatureId.Should().Be("sig-5", "most recent should be first");
        recent[1].SignatureId.Should().Be("sig-4");
        recent[2].SignatureId.Should().Be("sig-3");
    }

    [Fact]
    public void GetStats_ShouldCalculateCorrectStatistics()
    {
        // Arrange
        var store = new SignatureStore(TestHelpers.CreateMockLogger<SignatureStore>().Object);
        var httpContext = CreateTestHttpContext();

        // Add 3 bots (prob >= 0.7) and 2 humans (prob < 0.7)
        store.StoreSignature("bot-1", CreateTestEvidence(0.9), httpContext);
        store.StoreSignature("bot-2", CreateTestEvidence(0.85), httpContext);
        store.StoreSignature("bot-3", CreateTestEvidence(0.7), httpContext);
        store.StoreSignature("human-1", CreateTestEvidence(0.3), httpContext);
        store.StoreSignature("human-2", CreateTestEvidence(0.1), httpContext);

        // Act
        var stats = store.GetStats();

        // Assert
        stats.TotalSignatures.Should().Be(5);
        stats.BotCount.Should().Be(3);
        stats.HumanCount.Should().Be(2);
        stats.AvgBotProbability.Should().BeApproximately(0.57, 0.01); // (0.9+0.85+0.7+0.3+0.1)/5
    }

    [Fact]
    public void GetStats_ShouldReturnZeroStatsForEmptyStore()
    {
        // Arrange
        var store = new SignatureStore(TestHelpers.CreateMockLogger<SignatureStore>().Object);

        // Act
        var stats = store.GetStats();

        // Assert
        stats.TotalSignatures.Should().Be(0);
        stats.BotCount.Should().Be(0);
        stats.HumanCount.Should().Be(0);
        stats.AvgBotProbability.Should().Be(0.0);
    }

    [Fact]
    public void StoreSignature_ShouldExtractRequestMetadataCorrectly()
    {
        // Arrange
        var store = new SignatureStore(TestHelpers.CreateMockLogger<SignatureStore>().Object);
        var evidence = CreateTestEvidence(0.5);
        var httpContext = CreateTestHttpContext(
            path: "/api/test",
            userAgent: "TestBot/1.0",
            remoteIp: "192.168.1.100"
        );

        // Act
        store.StoreSignature("test-sig", evidence, httpContext);

        // Assert
        var stored = store.GetSignature("test-sig");
        stored.Should().NotBeNull();
        stored!.RequestMetadata.Path.Should().Be("/api/test");
        stored.RequestMetadata.UserAgent.Should().Be("TestBot/1.0");
        stored.RequestMetadata.RemoteIp.Should().Be("192.168.1.100");
    }

    [Fact]
    public void ConcurrentAccess_ShouldBeSafe()
    {
        // Arrange
        var store = new SignatureStore(TestHelpers.CreateMockLogger<SignatureStore>().Object, maxSignatures: 1000);
        var evidence = CreateTestEvidence(0.5);
        var httpContext = CreateTestHttpContext();
        var tasks = new List<Task>();

        // Act - Multiple threads storing signatures concurrently
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                store.StoreSignature($"sig-{index}", evidence, httpContext);
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert - All signatures should be stored successfully
        var stats = store.GetStats();
        stats.TotalSignatures.Should().Be(100);
    }

    private static AggregatedEvidence CreateTestEvidence(double botProbability) =>
        TestHelpers.CreateTestEvidence(botProbability);

    private static HttpContext CreateTestHttpContext(
        string path = "/test",
        string userAgent = "TestAgent/1.0",
        string remoteIp = "127.0.0.1")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers["User-Agent"] = userAgent;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);
        context.TraceIdentifier = Guid.NewGuid().ToString();
        return context;
    }
}
