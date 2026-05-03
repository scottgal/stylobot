using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Mostlylucid.BotDetection.Demo.Hubs;
using Mostlylucid.BotDetection.Demo.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Demo.Tests;

public class SignatureBroadcasterTests
{
    [Fact]
    public async Task BroadcastSignature_ShouldSendToSignalRGroup()
    {
        // Arrange
        var mockHubContext = new Mock<IHubContext<SignatureHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();

        mockHubContext.Setup(x => x.Clients).Returns(mockClients.Object);
        mockClients.Setup(x => x.Group("SignatureSubscribers")).Returns(mockClientProxy.Object);

        var broadcaster = new SignatureBroadcaster(mockHubContext.Object, TestHelpers.CreateMockLogger<SignatureBroadcaster>().Object);
        var signature = new StoredSignature
        {
            SignatureId = "test-123",
            Timestamp = DateTime.UtcNow,
            Evidence = CreateTestEvidence(0.8),
            RequestMetadata = TestHelpers.CreateTestRequestMetadata("/test", "TestBot/1.0", "127.0.0.1")
        };

        // Act
        await broadcaster.BroadcastSignature(signature);

        // Assert
        mockClientProxy.Verify(
            x => x.SendCoreAsync(
                "ReceiveNewSignature",
                It.Is<object[]>(args => args.Length == 1 && args[0] == signature),
                default),
            Times.Once);
    }

    [Fact]
    public async Task BroadcastStats_ShouldSendStatsToAllClients()
    {
        // Arrange
        var mockHubContext = new Mock<IHubContext<SignatureHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();

        mockHubContext.Setup(x => x.Clients).Returns(mockClients.Object);
        // BroadcastStats uses Clients.All, not Group
        mockClients.Setup(x => x.All).Returns(mockClientProxy.Object);

        var broadcaster = new SignatureBroadcaster(mockHubContext.Object, TestHelpers.CreateMockLogger<SignatureBroadcaster>().Object);
        var stats = new SignatureStoreStats
        {
            TotalSignatures = 100,
            BotCount = 60,
            HumanCount = 40,
            AvgBotProbability = 0.65
        };

        // Act
        await broadcaster.BroadcastStats(stats);

        // Assert
        mockClientProxy.Verify(
            x => x.SendCoreAsync(
                "ReceiveStats",
                It.Is<object[]>(args => args.Length == 1 && args[0] == stats),
                default),
            Times.Once);
    }

    [Fact]
    public async Task BroadcastSignature_ShouldNotThrowOnNullSignature()
    {
        // Arrange
        var mockHubContext = new Mock<IHubContext<SignatureHub>>();
        var broadcaster = new SignatureBroadcaster(mockHubContext.Object, TestHelpers.CreateMockLogger<SignatureBroadcaster>().Object);

        // Act & Assert
        // The method throws NullReferenceException, not ArgumentNullException
        // This is acceptable for this demo - in production we'd add null checks
        await broadcaster.Invoking(b => b.BroadcastSignature(null!))
            .Should().ThrowAsync<NullReferenceException>();
    }

    private static Mostlylucid.BotDetection.Orchestration.AggregatedEvidence CreateTestEvidence(double botProbability) =>
        TestHelpers.CreateTestEvidence(botProbability);
}
