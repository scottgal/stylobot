using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

public class FastPathDeciderTests
{
    private readonly Mock<IBotDetectionService> _mockFullDetector;
    private readonly Mock<ILearningEventBus> _mockLearningBus;
    private readonly Mock<IDetector> _mockUaDetector;
    private readonly BotDetectionOptions _options;

    public FastPathDeciderTests()
    {
        _options = new BotDetectionOptions();
        _mockUaDetector = new Mock<IDetector>();
        _mockUaDetector.Setup(x => x.Name).Returns("User-Agent Detector");
        _mockFullDetector = new Mock<IBotDetectionService>();
        _mockLearningBus = new Mock<ILearningEventBus>();

        // Setup learning bus to accept all publishes
        _mockLearningBus.Setup(x => x.TryPublish(It.IsAny<LearningEvent>())).Returns(true);
    }

    private FastPathDecider CreateDecider(Action<FastPathOptions>? configure = null)
    {
        configure?.Invoke(_options.FastPath);
        return new FastPathDecider(
            NullLogger<FastPathDecider>.Instance,
            Options.Create(_options),
            _mockUaDetector.Object,
            _mockFullDetector.Object,
            _mockLearningBus.Object);
    }

    private static HttpContext CreateHttpContext(string userAgent = "Mozilla/5.0", string path = "/api/test")
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = userAgent;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        context.TraceIdentifier = Guid.NewGuid().ToString();
        return context;
    }

    [Fact]
    public async Task DecideAndDetect_FastPathDisabled_RunsFullPath()
    {
        // Arrange
        var decider = CreateDecider(opt => opt.Enabled = false);
        var context = CreateHttpContext();

        var fullResult = new BotDetectionResult
        {
            IsBot = true,
            ConfidenceScore = 0.85,
            ProcessingTimeMs = 50
        };
        _mockFullDetector.Setup(x => x.DetectAsync(context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullResult);

        // Act
        var decision = await decider.DecideAndDetectAsync(context);

        // Assert
        Assert.Equal(DetectionMode.FullPath, decision.Mode);
        Assert.False(decision.ScheduledForFullAnalysis);
        _mockFullDetector.Verify(x => x.DetectAsync(context, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DecideAndDetect_AlwaysFullPath_RunsFullPathRegardlessOfUa()
    {
        // Arrange
        var decider = CreateDecider(opt =>
        {
            opt.Enabled = true;
            opt.AlwaysRunFullOnPaths = ["/login", "/checkout"];
        });
        var context = CreateHttpContext("BadBot/1.0", "/login");

        var fullResult = new BotDetectionResult
        {
            IsBot = true,
            ConfidenceScore = 0.95,
            ProcessingTimeMs = 50
        };
        _mockFullDetector.Setup(x => x.DetectAsync(context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullResult);

        // Act
        var decision = await decider.DecideAndDetectAsync(context);

        // Assert
        Assert.Equal(DetectionMode.FullPath, decision.Mode);
        Assert.Equal("/login", decision.RequestPath);
        _mockFullDetector.Verify(x => x.DetectAsync(context, It.IsAny<CancellationToken>()), Times.Once);
        // UA detector should not have been called
        _mockUaDetector.Verify(x => x.DetectAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DecideAndDetect_HighConfidenceUa_AbortsFastPath()
    {
        // Arrange
        var decider = CreateDecider(opt =>
        {
            opt.Enabled = true;
            opt.AbortThreshold = 0.95;
            opt.SampleRate = 0; // No sampling for this test
        });
        var context = CreateHttpContext("BadBot/1.0");

        _mockUaDetector.Setup(x => x.DetectAsync(context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectorResult
            {
                Confidence = 0.98,
                BotType = BotType.Scraper,
                BotName = "BadBot",
                Reasons = [new DetectionReason { Category = "UserAgent", Detail = "Known scraper pattern" }]
            });

        // Act
        var decision = await decider.DecideAndDetectAsync(context);

        // Assert
        Assert.Equal(DetectionMode.FastPath, decision.Mode);
        Assert.True(decision.Result.IsBot);
        Assert.Equal(0.98, decision.Result.ConfidenceScore);
        Assert.False(decision.ScheduledForFullAnalysis);

        // Full detector should NOT have been called
        _mockFullDetector.Verify(x => x.DetectAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // MinimalDetection event should have been published
        _mockLearningBus.Verify(x => x.TryPublish(It.Is<LearningEvent>(e =>
            e.Type == LearningEventType.MinimalDetection)), Times.Once);
    }

    [Fact]
    public async Task DecideAndDetect_HighConfidenceUa_Sampled_SchedulesFullAnalysis()
    {
        // Arrange
        var decider = CreateDecider(opt =>
        {
            opt.Enabled = true;
            opt.AbortThreshold = 0.95;
            opt.SampleRate = 1.0; // Always sample for this test
        });
        var context = CreateHttpContext("BadBot/1.0");

        _mockUaDetector.Setup(x => x.DetectAsync(context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectorResult
            {
                Confidence = 0.98,
                BotType = BotType.Scraper,
                BotName = "BadBot",
                Reasons = [new DetectionReason { Category = "UserAgent", Detail = "Known scraper pattern" }]
            });

        // Act
        var decision = await decider.DecideAndDetectAsync(context);

        // Assert
        Assert.Equal(DetectionMode.FastPathSampled, decision.Mode);
        Assert.True(decision.ScheduledForFullAnalysis);

        // Both MinimalDetection and FullAnalysisRequest should have been published
        _mockLearningBus.Verify(x => x.TryPublish(It.Is<LearningEvent>(e =>
            e.Type == LearningEventType.MinimalDetection)), Times.Once);
        _mockLearningBus.Verify(x => x.TryPublish(It.Is<LearningEvent>(e =>
            e.Type == LearningEventType.FullAnalysisRequest)), Times.Once);
    }

    [Fact]
    public async Task DecideAndDetect_LowConfidenceUa_RunsFullPath()
    {
        // Arrange
        var decider = CreateDecider(opt =>
        {
            opt.Enabled = true;
            opt.AbortThreshold = 0.95;
        });
        var context = CreateHttpContext("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0");

        _mockUaDetector.Setup(x => x.DetectAsync(context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectorResult
            {
                Confidence = 0.2, // Low confidence - not a bot
                BotType = null,
                BotName = null,
                Reasons = new List<DetectionReason>()
            });

        var fullResult = new BotDetectionResult
        {
            IsBot = false,
            ConfidenceScore = 0.15,
            ProcessingTimeMs = 50
        };
        _mockFullDetector.Setup(x => x.DetectAsync(context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullResult);

        // Act
        var decision = await decider.DecideAndDetectAsync(context);

        // Assert
        Assert.Equal(DetectionMode.FullPath, decision.Mode);
        Assert.False(decision.Result.IsBot);

        // Full detector should have been called
        _mockFullDetector.Verify(x => x.DetectAsync(context, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DecideAndDetect_MediumConfidenceUa_RunsFullPath()
    {
        // Arrange
        var decider = CreateDecider(opt =>
        {
            opt.Enabled = true;
            opt.AbortThreshold = 0.95;
        });
        var context = CreateHttpContext("SuspiciousBot/1.0");

        // UA detector returns medium confidence (below abort threshold)
        _mockUaDetector.Setup(x => x.DetectAsync(context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectorResult
            {
                Confidence = 0.75, // Medium confidence
                BotType = BotType.Unknown,
                BotName = "SuspiciousBot",
                Reasons = [new DetectionReason { Category = "UserAgent", Detail = "Suspicious pattern" }]
            });

        var fullResult = new BotDetectionResult
        {
            IsBot = true,
            ConfidenceScore = 0.85,
            ProcessingTimeMs = 50
        };
        _mockFullDetector.Setup(x => x.DetectAsync(context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullResult);

        // Act
        var decision = await decider.DecideAndDetectAsync(context);

        // Assert
        Assert.Equal(DetectionMode.FullPath, decision.Mode);
        // Full detection result should be used
        Assert.Equal(0.85, decision.Result.ConfidenceScore);
    }

    [Fact]
    public async Task DecideAndDetect_PublishesCorrectMetadata()
    {
        // Arrange
        LearningEvent? capturedEvent = null;
        _mockLearningBus.Setup(x => x.TryPublish(It.IsAny<LearningEvent>()))
            .Callback<LearningEvent>(e => capturedEvent = e)
            .Returns(true);

        var decider = CreateDecider(opt =>
        {
            opt.Enabled = true;
            opt.AbortThreshold = 0.95;
            opt.SampleRate = 0;
        });
        var context = CreateHttpContext("BadBot/1.0", "/api/data");

        _mockUaDetector.Setup(x => x.DetectAsync(context, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DetectorResult
            {
                Confidence = 0.98,
                BotType = BotType.Scraper,
                BotName = "BadBot",
                Reasons = [new DetectionReason { Category = "UserAgent", Detail = "Known scraper" }]
            });

        // Act
        await decider.DecideAndDetectAsync(context);

        // Assert
        Assert.NotNull(capturedEvent);
        Assert.Equal(LearningEventType.MinimalDetection, capturedEvent.Type);
        Assert.Equal(0.98, capturedEvent.Confidence);
        Assert.True(capturedEvent.Label);
        Assert.NotNull(capturedEvent.Metadata);
        Assert.Equal("BadBot/1.0", capturedEvent.Metadata["userAgent"]);
        Assert.Equal("/api/data", capturedEvent.Metadata["path"]);
        Assert.Equal("FastPath", capturedEvent.Metadata["mode"]);
    }
}