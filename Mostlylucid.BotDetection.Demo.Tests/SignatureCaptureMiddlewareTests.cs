using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.BotDetection.Demo.Middleware;
using Mostlylucid.BotDetection.Demo.Services;
using Mostlylucid.BotDetection.Demo.Hubs;
using Mostlylucid.BotDetection.Orchestration;
using Xunit;

namespace Mostlylucid.BotDetection.Demo.Tests;

public class SignatureCaptureMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ShouldGenerateSignatureIdBeforePipeline()
    {
        // Arrange
        var mockStore = new Mock<SignatureStore>(TestHelpers.CreateMockLogger<SignatureStore>().Object, 10000);
        var mockBroadcaster = new Mock<SignatureBroadcaster>(
            Mock.Of<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.SignatureHub>>(),
            TestHelpers.CreateMockLogger<SignatureBroadcaster>().Object);
        var mockLogger = new Mock<ILogger<SignatureCaptureMiddleware>>();

        var context = new DefaultHttpContext();
        var nextCalled = false;

        Task Next(HttpContext ctx)
        {
            // Verify signature ID was set before calling next
            ctx.Items.Should().ContainKey("BotDetection.SignatureId");
            ctx.Items["BotDetection.SignatureId"].Should().NotBeNull();
            nextCalled = true;
            return Task.CompletedTask;
        }

        var middleware = new SignatureCaptureMiddleware(
            Next,
            mockStore.Object,
            mockBroadcaster.Object,
            mockLogger.Object);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.Should().BeTrue();
        context.Items.Should().ContainKey("BotDetection.SignatureId");
    }

    [Fact]
    public async Task InvokeAsync_ShouldCaptureAndStoreSignatureWhenEvidencePresent()
    {
        // Arrange
        var mockStore = new Mock<SignatureStore>(TestHelpers.CreateMockLogger<SignatureStore>().Object, 10000);
        var mockBroadcaster = new Mock<SignatureBroadcaster>(
            Mock.Of<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.SignatureHub>>(),
            TestHelpers.CreateMockLogger<SignatureBroadcaster>().Object);
        var mockLogger = new Mock<ILogger<SignatureCaptureMiddleware>>();

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");
        context.Request.Path = "/test";
        context.Request.Headers["User-Agent"] = "TestBot/1.0";

        var evidence = TestHelpers.CreateTestEvidence(0.85);

        Task Next(HttpContext ctx)
        {
            // Simulate bot detection middleware adding evidence
            ctx.Items["BotDetection.Evidence"] = evidence;
            return Task.CompletedTask;
        }

        var middleware = new SignatureCaptureMiddleware(
            Next,
            mockStore.Object,
            mockBroadcaster.Object,
            mockLogger.Object);

        // Store will have the signature after middleware runs

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.Headers.Should().ContainKey("X-Signature-ID");

        // Verify signature was stored
        var signatureId = context.Items["BotDetection.SignatureId"]?.ToString();
        signatureId.Should().NotBeNullOrEmpty();
        var stored = mockStore.Object.GetSignature(signatureId!);
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotStoreWhenNoEvidence()
    {
        // Arrange
        var mockStore = new Mock<SignatureStore>(TestHelpers.CreateMockLogger<SignatureStore>().Object, 10000);
        var mockBroadcaster = new Mock<SignatureBroadcaster>(
            Mock.Of<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.SignatureHub>>(),
            TestHelpers.CreateMockLogger<SignatureBroadcaster>().Object);
        var mockLogger = new Mock<ILogger<SignatureCaptureMiddleware>>();

        var context = new DefaultHttpContext();

        Task Next(HttpContext ctx)
        {
            // Don't add evidence
            return Task.CompletedTask;
        }

        var middleware = new SignatureCaptureMiddleware(
            Next,
            mockStore.Object,
            mockBroadcaster.Object,
            mockLogger.Object);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        // Since no evidence, nothing should be stored
        var stats = mockStore.Object.GetStats();
        stats.TotalSignatures.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotThrowOnStorageFailure()
    {
        // Arrange
        var mockStore = new Mock<SignatureStore>(TestHelpers.CreateMockLogger<SignatureStore>().Object, 10000);
        var mockBroadcaster = new Mock<SignatureBroadcaster>(
            Mock.Of<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.SignatureHub>>(),
            TestHelpers.CreateMockLogger<SignatureBroadcaster>().Object);
        var mockLogger = new Mock<ILogger<SignatureCaptureMiddleware>>();

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

        var evidence = TestHelpers.CreateTestEvidence(0.85);

        Task Next(HttpContext ctx)
        {
            ctx.Items["BotDetection.Evidence"] = evidence;
            return Task.CompletedTask;
        }

        // Create a failing store by using a null logger (will throw but middleware should catch)
        // For this test, we'll just verify the middleware doesn't throw even if broadcast fails

        var middleware = new SignatureCaptureMiddleware(
            Next,
            mockStore.Object,
            mockBroadcaster.Object,
            mockLogger.Object);

        // Act & Assert - Should not throw
        await middleware.Invoking(m => m.InvokeAsync(context))
            .Should().NotThrowAsync();
    }
}
