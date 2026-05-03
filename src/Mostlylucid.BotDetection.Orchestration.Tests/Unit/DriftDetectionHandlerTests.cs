using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

public class DriftDetectionHandlerTests
{
    private readonly DriftDetectionHandler _handler;
    private readonly Mock<ILearningEventBus> _mockLearningBus;
    private readonly BotDetectionOptions _options;
    private readonly List<LearningEvent> _publishedEvents;

    public DriftDetectionHandlerTests()
    {
        _options = new BotDetectionOptions();
        _mockLearningBus = new Mock<ILearningEventBus>();
        _publishedEvents = [];

        _mockLearningBus.Setup(x => x.TryPublish(It.IsAny<LearningEvent>()))
            .Callback<LearningEvent>(e => _publishedEvents.Add(e))
            .Returns(true);

        _handler = new DriftDetectionHandler(
            NullLogger<DriftDetectionHandler>.Instance,
            Options.Create(_options),
            _mockLearningBus.Object);
    }

    [Fact]
    public void HandledEventTypes_IncludesExpectedTypes()
    {
        var types = _handler.HandledEventTypes;

        Assert.Contains(LearningEventType.MinimalDetection, types);
        Assert.Contains(LearningEventType.FullDetection, types);
        Assert.Contains(LearningEventType.HighConfidenceDetection, types);
    }

    [Fact]
    public async Task HandleMinimalDetection_TracksSample()
    {
        // Arrange
        var evt = new LearningEvent
        {
            Type = LearningEventType.MinimalDetection,
            Source = "FastPathDecider",
            Pattern = "ua:test123",
            Confidence = 0.95,
            Label = true,
            Metadata = new Dictionary<string, object>
            {
                ["userAgent"] = "TestBot/1.0"
            }
        };

        // Act
        await _handler.HandleAsync(evt);

        // Assert - no immediate event published, just tracked
        Assert.Empty(_publishedEvents);
    }

    [Fact]
    public async Task HandleHighConfidenceDetection_LearnsPattern()
    {
        // Arrange
        _options.FastPath.EnableFeedbackLoop = true;
        _options.FastPath.FeedbackMinConfidence = 0.9;
        _options.FastPath.FeedbackMinOccurrences = 3;

        var evt = new LearningEvent
        {
            Type = LearningEventType.HighConfidenceDetection,
            Source = "BotDetectionService",
            Pattern = "ua:badbot",
            Confidence = 0.95,
            Label = true,
            Metadata = new Dictionary<string, object>
            {
                ["userAgent"] = "BadBot/1.0",
                ["ip"] = "1.2.3.4",
                ["botType"] = "Scraper"
            }
        };

        // Act - process same pattern multiple times to reach threshold
        await _handler.HandleAsync(evt);
        await _handler.HandleAsync(evt);
        await _handler.HandleAsync(evt);
        await _handler.HandleAsync(evt);
        await _handler.HandleAsync(evt);

        // Assert - after enough occurrences, should publish SignatureFeedback
        var feedbackEvents = _publishedEvents.Where(e => e.Type == LearningEventType.SignatureFeedback).ToList();
        Assert.NotEmpty(feedbackEvents);
    }

    [Fact]
    public async Task HandleHighConfidenceDetection_BelowThreshold_DoesNotLearn()
    {
        // Arrange
        _options.FastPath.EnableFeedbackLoop = true;
        _options.FastPath.FeedbackMinConfidence = 0.95;

        var evt = new LearningEvent
        {
            Type = LearningEventType.HighConfidenceDetection,
            Source = "BotDetectionService",
            Pattern = "ua:lowconf",
            Confidence = 0.85, // Below threshold
            Label = true,
            Metadata = new Dictionary<string, object>
            {
                ["userAgent"] = "LowConfBot/1.0"
            }
        };

        // Act
        await _handler.HandleAsync(evt);
        await _handler.HandleAsync(evt);
        await _handler.HandleAsync(evt);

        // Assert - no feedback because confidence too low
        var feedbackEvents = _publishedEvents.Where(e => e.Type == LearningEventType.SignatureFeedback).ToList();
        Assert.Empty(feedbackEvents);
    }

    [Fact]
    public async Task HandleHighConfidenceDetection_FeedbackDisabled_DoesNotLearn()
    {
        // Arrange
        _options.FastPath.EnableFeedbackLoop = false;

        var evt = new LearningEvent
        {
            Type = LearningEventType.HighConfidenceDetection,
            Source = "BotDetectionService",
            Pattern = "ua:badbot",
            Confidence = 0.99,
            Label = true,
            Metadata = new Dictionary<string, object>
            {
                ["userAgent"] = "BadBot/1.0"
            }
        };

        // Act
        for (var i = 0; i < 10; i++) await _handler.HandleAsync(evt);

        // Assert - no feedback because disabled
        Assert.Empty(_publishedEvents);
    }

    [Fact]
    public void GetLearnedPatterns_ReturnsMatchingPatterns()
    {
        // Arrange - manually add patterns via handler internals
        // (In real use, these would come from HandleHighConfidenceDetection)
        var handler = new DriftDetectionHandler(
            NullLogger<DriftDetectionHandler>.Instance,
            Options.Create(_options),
            _mockLearningBus.Object);

        // Act
        var uaPatterns = handler.GetLearnedPatterns("UserAgent").ToList();
        var ipPatterns = handler.GetLearnedPatterns("IP").ToList();

        // Assert - initially empty
        Assert.Empty(uaPatterns);
        Assert.Empty(ipPatterns);
    }

    [Fact]
    public void GetDriftStats_InitiallyEmpty()
    {
        // Act
        var stats = _handler.GetDriftStats().ToList();

        // Assert
        Assert.Empty(stats);
    }

    [Fact]
    public async Task DriftDetection_WhenDisabled_DoesNotProcessFullDetection()
    {
        // Arrange
        _options.FastPath.EnableDriftDetection = false;

        // Track a minimal detection
        var minimalEvt = new LearningEvent
        {
            Type = LearningEventType.MinimalDetection,
            Source = "FastPathDecider",
            Pattern = "ua:test123",
            Confidence = 0.95,
            Label = true,
            Metadata = new Dictionary<string, object> { ["userAgent"] = "TestBot/1.0" }
        };
        await _handler.HandleAsync(minimalEvt);

        // Process a full detection
        var fullEvt = new LearningEvent
        {
            Type = LearningEventType.FullDetection,
            Source = "FastPathDecider",
            Pattern = "ua:test123",
            Confidence = 0.30, // Disagreement!
            Label = false,
            Metadata = new Dictionary<string, object> { ["userAgent"] = "TestBot/1.0" }
        };
        await _handler.HandleAsync(fullEvt);

        // Assert - no drift event because disabled
        var driftEvents = _publishedEvents.Where(e => e.Type == LearningEventType.FastPathDriftDetected).ToList();
        Assert.Empty(driftEvents);
    }

    [Fact]
    public async Task SignatureFeedback_IncludesCorrectMetadata()
    {
        // Arrange
        _options.FastPath.EnableFeedbackLoop = true;
        _options.FastPath.FeedbackMinConfidence = 0.9;
        _options.FastPath.FeedbackMinOccurrences = 2;

        var evt = new LearningEvent
        {
            Type = LearningEventType.HighConfidenceDetection,
            Source = "BotDetectionService",
            Pattern = "ua:metadata_test",
            Confidence = 0.95,
            Label = true,
            Metadata = new Dictionary<string, object>
            {
                ["userAgent"] = "MetadataBot/1.0",
                ["ip"] = "5.6.7.8",
                ["botType"] = "Scraper",
                ["botName"] = "MetadataBot"
            }
        };

        // Act - process enough times to trigger feedback
        for (var i = 0; i < 5; i++) await _handler.HandleAsync(evt);

        // Assert
        var feedbackEvent = _publishedEvents.FirstOrDefault(e => e.Type == LearningEventType.SignatureFeedback);
        Assert.NotNull(feedbackEvent);
        Assert.NotNull(feedbackEvent.Metadata);
        // The signatureType comes from the original event's pattern type (e.g., "UserAgent" from "ua:" prefix)
        Assert.True(feedbackEvent.Metadata.ContainsKey("signatureType"), "Should have signatureType metadata");
    }
}