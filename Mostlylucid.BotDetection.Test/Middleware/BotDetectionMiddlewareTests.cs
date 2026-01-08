using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Test.Helpers;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Middleware;

/// <summary>
///     Comprehensive tests for BotDetectionMiddleware
/// </summary>
public class BotDetectionMiddlewareTests
{
    private readonly ILogger<BotDetectionMiddleware> _logger;

    public BotDetectionMiddlewareTests()
    {
        _logger = new Mock<ILogger<BotDetectionMiddleware>>().Object;
    }

    private BotDetectionMiddleware CreateMiddleware(
        RequestDelegate next,
        BotDetectionOptions? options = null)
    {
        return new BotDetectionMiddleware(
            next,
            _logger,
            Options.Create(options ?? new BotDetectionOptions()));
    }

    private static AggregatedEvidence CreateEvidence(
        double botProbability = 0.1,
        double confidence = 0.9,
        RiskBand riskBand = RiskBand.Low,
        BotType? botType = null,
        string? botName = null,
        PolicyAction? policyAction = null)
    {
        return new AggregatedEvidence
        {
            BotProbability = botProbability,
            Confidence = confidence,
            RiskBand = riskBand,
            PrimaryBotType = botType,
            PrimaryBotName = botName,
            PolicyAction = policyAction,
            // Contributions is now derived from Ledger - no need to set
            Signals = new Dictionary<string, object>(),
            CategoryBreakdown = new Dictionary<string, CategoryScore>(),
            ContributingDetectors = new HashSet<string>()
        };
    }

    private static Mock<BlackboardOrchestrator> CreateMockOrchestrator(AggregatedEvidence? result = null)
    {
        // Constructor: logger, options, detectors, learningBus?, policyRegistry?, policyEvaluator?, signatureCoordinator?
        var mock = new Mock<BlackboardOrchestrator>(
            Mock.Of<ILogger<BlackboardOrchestrator>>(),
            Options.Create(new BotDetectionOptions()),
            Enumerable.Empty<IContributingDetector>(),
            null, // learningBus
            null, // policyRegistry
            null, // policyEvaluator
            null // signatureCoordinator
        );

        var evidence = result ?? CreateEvidence();

        mock.Setup(o => o.DetectWithPolicyAsync(
                It.IsAny<HttpContext>(),
                It.IsAny<DetectionPolicy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(evidence);

        return mock;
    }

    private static Mock<IPolicyRegistry> CreateMockPolicyRegistry()
    {
        var mock = new Mock<IPolicyRegistry>();
        mock.Setup(p => p.GetPolicyForPath(It.IsAny<string>()))
            .Returns(DetectionPolicy.Default);
        mock.Setup(p => p.GetPolicy(It.IsAny<string>()))
            .Returns(DetectionPolicy.Default);
        return mock;
    }

    private static Mock<IActionPolicyRegistry> CreateMockActionPolicyRegistry()
    {
        var mock = new Mock<IActionPolicyRegistry>();
        return mock;
    }

    #region Security Tests

    [Fact]
    public async Task InvokeAsync_TestModeDisabled_DoesNotLeakTestModeInfo()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;
        var mockOrchestrator = CreateMockOrchestrator(CreateEvidence(
            0.1,
            0.9,
            RiskBand.Low));
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();

        var options = new BotDetectionOptions { EnableTestMode = false };
        var middleware = CreateMiddleware(next, options);

        // Try various test mode values
        var testModes = new[] { "disable", "bot", "human", "googlebot", "malicious" };

        foreach (var testMode in testModes)
        {
            var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
            {
                { "ml-bot-test-mode", testMode }
            });

            // Act
            await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
                mockActionPolicyRegistry.Object, null);

            // Assert - no test mode info should be leaked
            Assert.False(context.Response.Headers.ContainsKey("X-Test-Mode"),
                $"Test mode header leaked for mode '{testMode}'");
        }
    }

    #endregion


    #region Normal Detection Flow Tests

    [Fact]
    public async Task InvokeAsync_NormalRequest_CallsOrchestrator()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var mockOrchestrator = CreateMockOrchestrator(CreateEvidence(
            0.1,
            0.9,
            RiskBand.Low));
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();

        var middleware = CreateMiddleware(next);
        var context = MockHttpContext.CreateRealisticBrowser();

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert
        Assert.True(nextCalled, "Next middleware should be called");
        mockOrchestrator.Verify(
            o => o.DetectWithPolicyAsync(context, It.IsAny<DetectionPolicy>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_BotDetected_AddsHeadersAndResult()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;

        var mockOrchestrator = CreateMockOrchestrator(CreateEvidence(
            0.95,
            0.9,
            RiskBand.High,
            BotType.Scraper,
            "TestBot"));
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();

        // Enable response headers to verify they are added
        var options = new BotDetectionOptions
        {
            ResponseHeaders = new ResponseHeadersOptions { Enabled = true }
        };
        var middleware = CreateMiddleware(next, options);
        var context = MockHttpContext.CreateWithUserAgent("TestBot/1.0");

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - Response headers should be added when ResponseHeaders.Enabled = true
        Assert.True(context.Response.Headers.ContainsKey("X-Bot-Risk-Score"));
        Assert.True(context.Response.Headers.ContainsKey("X-Bot-Risk-Band"));
        Assert.True(context.Items.ContainsKey(BotDetectionMiddleware.BotDetectionResultKey));
        Assert.True(context.Items.ContainsKey(BotDetectionMiddleware.AggregatedEvidenceKey));
    }

    [Fact]
    public async Task InvokeAsync_HumanDetected_NoBlockingHeaders()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;

        var mockOrchestrator = CreateMockOrchestrator(CreateEvidence(
            0.1,
            0.9,
            RiskBand.Low));
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();

        var middleware = CreateMiddleware(next);
        var context = MockHttpContext.CreateRealisticBrowser();

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - no blocking headers, but result should be stored
        Assert.False(context.Response.Headers.ContainsKey("X-Bot-Detected"));
        Assert.True(context.Items.ContainsKey(BotDetectionMiddleware.BotDetectionResultKey));
        Assert.True(context.Items.ContainsKey(BotDetectionMiddleware.AggregatedEvidenceKey));
    }

    [Fact]
    public async Task InvokeAsync_StoresResultInHttpContext()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;
        var expectedEvidence = CreateEvidence(
            0.8,
            0.85,
            RiskBand.Medium,
            BotType.SearchEngine,
            "Googlebot");

        var mockOrchestrator = CreateMockOrchestrator(expectedEvidence);
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();

        var middleware = CreateMiddleware(next);
        var context = MockHttpContext.CreateGooglebot();

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - legacy result created from aggregated evidence
        var storedResult = context.Items[BotDetectionMiddleware.BotDetectionResultKey] as BotDetectionResult;
        Assert.NotNull(storedResult);
        Assert.True(storedResult.IsBot); // 0.8 >= 0.5
        Assert.Equal(expectedEvidence.BotProbability, storedResult.ConfidenceScore);
        Assert.Equal(expectedEvidence.PrimaryBotName, storedResult.BotName);

        // Assert - aggregated evidence also stored
        var storedEvidence = context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] as AggregatedEvidence;
        Assert.NotNull(storedEvidence);
        Assert.Equal(expectedEvidence.BotProbability, storedEvidence.BotProbability);
    }

    #endregion

    #region Test Mode Tests

    [Fact]
    public async Task InvokeAsync_TestModeDisabled_IgnoresTestHeader()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;

        var mockOrchestrator = CreateMockOrchestrator(CreateEvidence(
            0.1,
            0.9,
            RiskBand.Low));
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();

        var options = new BotDetectionOptions { EnableTestMode = false };
        var middleware = CreateMiddleware(next, options);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            { "ml-bot-test-mode", "bot" }
        });

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - orchestrator should be called (test header ignored)
        mockOrchestrator.Verify(
            o => o.DetectWithPolicyAsync(context, It.IsAny<DetectionPolicy>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.False(context.Response.Headers.ContainsKey("X-Test-Mode"));
    }

    [Fact]
    public async Task InvokeAsync_TestModeEnabled_DisableBypassesDetection()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var mockOrchestrator = CreateMockOrchestrator();
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();
        var options = new BotDetectionOptions { EnableTestMode = true };
        var middleware = CreateMiddleware(next, options);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            { "ml-bot-test-mode", "disable" }
        });

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - "disable" mode skips detection entirely, no result stored
        mockOrchestrator.Verify(
            o => o.DetectWithPolicyAsync(It.IsAny<HttpContext>(), It.IsAny<DetectionPolicy>(),
                It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(nextCalled);
        Assert.Equal("disabled", context.Response.Headers["X-Test-Mode"]);
        // No result stored when disabled
    }

    [Theory]
    [InlineData("googlebot", "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("bingbot", "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
    [InlineData("human", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36")]
    public async Task InvokeAsync_TestModeEnabled_RunsRealDetectionWithSimulatedUA(
        string testMode, string expectedSimulatedUA)
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;
        var mockOrchestrator = CreateMockOrchestrator();
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();
        var options = new BotDetectionOptions
        {
            EnableTestMode = true,
            TestModeSimulations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["googlebot"] = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
                ["bingbot"] = "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",
                ["human"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
            }
        };
        var middleware = CreateMiddleware(next, options);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            { "ml-bot-test-mode", testMode }
        });

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - real orchestrator should be called (test mode now runs real detection)
        mockOrchestrator.Verify(
            o => o.DetectWithPolicyAsync(It.IsAny<HttpContext>(), It.IsAny<DetectionPolicy>(),
                It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("true", context.Response.Headers["X-Test-Mode"]);
    }

    [Fact]
    public async Task InvokeAsync_TestModeEnabled_UnknownModeRunsRealDetectionWithOriginalUA()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;
        var mockOrchestrator = CreateMockOrchestrator();
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();
        var options = new BotDetectionOptions { EnableTestMode = true };
        var middleware = CreateMiddleware(next, options);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            { "ml-bot-test-mode", "custom-bot-type" }
        });

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - unknown test mode still runs real detection (no UA override if not configured)
        mockOrchestrator.Verify(
            o => o.DetectWithPolicyAsync(It.IsAny<HttpContext>(), It.IsAny<DetectionPolicy>(),
                It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("true", context.Response.Headers["X-Test-Mode"]);
    }

    [Fact]
    public async Task InvokeAsync_TestModeEnabled_AddsTestModeHeader()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;
        var mockOrchestrator = CreateMockOrchestrator();
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();
        var options = new BotDetectionOptions
        {
            EnableTestMode = true,
            TestModeSimulations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["googlebot"] = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"
            }
        };
        var middleware = CreateMiddleware(next, options);
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            { "ml-bot-test-mode", "googlebot" }
        });

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - test mode header and simulated UA header are set
        Assert.Equal("true", context.Response.Headers["X-Test-Mode"]);
        Assert.True(context.Response.Headers.ContainsKey("X-Test-Simulated-UA"));
    }

    [Fact]
    public async Task InvokeAsync_TestModeEnabled_NoHeader_UsesNormalDetection()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;
        var mockOrchestrator = CreateMockOrchestrator(CreateEvidence(
            0.2,
            0.8,
            RiskBand.Low));
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();

        var options = new BotDetectionOptions { EnableTestMode = true };
        var middleware = CreateMiddleware(next, options);
        var context = MockHttpContext.CreateRealisticBrowser();

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - should use normal detection via orchestrator
        mockOrchestrator.Verify(
            o => o.DetectWithPolicyAsync(context, It.IsAny<DetectionPolicy>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.False(context.Response.Headers.ContainsKey("X-Test-Mode"));
    }

    #endregion

    #region Pipeline Tests

    [Fact]
    public async Task InvokeAsync_BotBelowThreshold_CallsNextMiddleware()
    {
        // Arrange
        var nextCallCount = 0;
        RequestDelegate next = _ =>
        {
            nextCallCount++;
            return Task.CompletedTask;
        };

        // Low bot probability - should NOT block
        var mockOrchestrator = CreateMockOrchestrator(CreateEvidence(
            0.3,
            0.8,
            RiskBand.Low));
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();

        var middleware = CreateMiddleware(next);
        var context = MockHttpContext.CreateSuspiciousBot();

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - next should be called when bot probability is low
        Assert.Equal(1, nextCallCount);
    }

    [Fact]
    public async Task InvokeAsync_BotAboveThreshold_BlocksAndDoesNotCallNext()
    {
        // Arrange
        var nextCallCount = 0;
        RequestDelegate next = _ =>
        {
            nextCallCount++;
            return Task.CompletedTask;
        };

        // High bot probability with Block action - should block
        var mockOrchestrator = CreateMockOrchestrator(CreateEvidence(
            1.0,
            1.0,
            RiskBand.VeryHigh,
            policyAction: PolicyAction.Block));
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();

        var middleware = CreateMiddleware(next);
        var context = MockHttpContext.CreateSuspiciousBot();

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert - next should NOT be called when bot is blocked
        Assert.Equal(0, nextCallCount);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_PassesCancellationToken()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;
        CancellationToken capturedToken = default;

        // Constructor: logger, options, detectors, learningBus?, policyRegistry?, policyEvaluator?, signatureCoordinator?
        var mockOrchestrator = new Mock<BlackboardOrchestrator>(
            Mock.Of<ILogger<BlackboardOrchestrator>>(),
            Options.Create(new BotDetectionOptions()),
            Enumerable.Empty<IContributingDetector>(),
            Mock.Of<ILearningEventBus>(), // learningBus
            Mock.Of<IPolicyRegistry>(), // policyRegistry
            Mock.Of<IPolicyEvaluator>(), // policyEvaluator
            null // signatureCoordinator (optional)
        );

        mockOrchestrator.Setup(o => o.DetectWithPolicyAsync(
                It.IsAny<HttpContext>(),
                It.IsAny<DetectionPolicy>(),
                It.IsAny<CancellationToken>()))
            .Callback<HttpContext, DetectionPolicy, CancellationToken>((_, _, ct) => capturedToken = ct)
            .ReturnsAsync(CreateEvidence(
                0.1,
                0.9,
                RiskBand.Low));

        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = CreateMockActionPolicyRegistry();

        var middleware = CreateMiddleware(next);
        var context = MockHttpContext.CreateRealisticBrowser();

        using var cts = new CancellationTokenSource();
        context.RequestAborted = cts.Token;

        // Act
        await middleware.InvokeAsync(context, mockOrchestrator.Object, mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object, null);

        // Assert
        Assert.Equal(cts.Token, capturedToken);
    }

    #endregion
}