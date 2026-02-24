using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Attributes;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Middleware;

public class BotDetectionMiddlewarePiiMaskingTests
{
    private static Mock<BlackboardOrchestrator> CreateMockOrchestrator(AggregatedEvidence result)
    {
        var mock = new Mock<BlackboardOrchestrator>(
            Mock.Of<ILogger<BlackboardOrchestrator>>(),
            Options.Create(new BotDetectionOptions()),
            Enumerable.Empty<IContributingDetector>(),
            new PiiHasher(new byte[32]),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        mock.Setup(o => o.DetectWithPolicyAsync(
                It.IsAny<HttpContext>(),
                It.IsAny<DetectionPolicy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        return mock;
    }

    private static Mock<IPolicyRegistry> CreateMockPolicyRegistry()
    {
        var mock = new Mock<IPolicyRegistry>();
        mock.Setup(p => p.GetPolicyForPath(It.IsAny<string>())).Returns(DetectionPolicy.Default);
        mock.Setup(p => p.GetPolicy(It.IsAny<string>())).Returns(DetectionPolicy.Default);
        return mock;
    }

    [Fact]
    public async Task InvokeAsync_MaskPiiAction_RedactsResponseAndEmitsSignals()
    {
        // Arrange
        const string responseBody = "{\"email\":\"alice@example.com\",\"phone\":\"+1 (555) 123-4567\"}";
        RequestDelegate next = async context =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(responseBody);
        };

        var options = new BotDetectionOptions
        {
            BotThreshold = 0.7,
            ResponsePiiMasking = new ResponsePiiMaskingOptions
            {
                Enabled = true
            }
        };
        var middleware = new BotDetectionMiddleware(
            next,
            Mock.Of<ILogger<BotDetectionMiddleware>>(),
            Options.Create(options));

        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.99,
            Confidence = 0.99,
            RiskBand = RiskBand.VeryHigh,
            TriggeredActionPolicyName = "mask-pii",
            Signals = new Dictionary<string, object>(),
            ContributingDetectors = new HashSet<string>()
        };

        var mockOrchestrator = CreateMockOrchestrator(evidence);
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = new Mock<IActionPolicyRegistry>();
        mockActionPolicyRegistry
            .Setup(r => r.GetPolicy("mask-pii"))
            .Returns(new MarkerActionPolicy("mask-pii", "mask-pii"));

        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            },
            RequestServices = new ServiceCollection()
                .AddSingleton<IResponsePiiMasker, MicrosoftRecognizersResponsePiiMasker>()
                .BuildServiceProvider()
        };

        // Act
        await middleware.InvokeAsync(
            context,
            mockOrchestrator.Object,
            mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object,
            null!);

        // Assert
        context.Response.Body.Position = 0;
        var masked = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Contains("[REDACTED:PII]", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", masked, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(true, context.Items["BotDetection.ResponsePiiMasking.Attempted"]);
        Assert.Equal(true, context.Items["BotDetection.ResponsePiiMasking.Masked"]);
        Assert.True((int)context.Items["BotDetection.ResponsePiiMasking.RedactionCount"]! > 0);
    }

    [Fact]
    public async Task InvokeAsync_MaskPiiAction_LargeBodyFailsOpen()
    {
        // Arrange
        var largePayload = new string('a', 270_000) + " alice@example.com";
        RequestDelegate next = async context =>
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(largePayload);
        };

        var middleware = new BotDetectionMiddleware(
            next,
            Mock.Of<ILogger<BotDetectionMiddleware>>(),
            Options.Create(new BotDetectionOptions
            {
                ResponsePiiMasking = new ResponsePiiMaskingOptions
                {
                    Enabled = true
                }
            }));

        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.99,
            Confidence = 0.99,
            RiskBand = RiskBand.VeryHigh,
            TriggeredActionPolicyName = "mask-pii",
            Signals = new Dictionary<string, object>(),
            ContributingDetectors = new HashSet<string>()
        };

        var mockOrchestrator = CreateMockOrchestrator(evidence);
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = new Mock<IActionPolicyRegistry>();
        mockActionPolicyRegistry
            .Setup(r => r.GetPolicy("mask-pii"))
            .Returns(new MarkerActionPolicy("mask-pii", "mask-pii"));

        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            },
            RequestServices = new ServiceCollection()
                .AddSingleton<IResponsePiiMasker, MicrosoftRecognizersResponsePiiMasker>()
                .BuildServiceProvider()
        };

        // Act
        await middleware.InvokeAsync(
            context,
            mockOrchestrator.Object,
            mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object,
            null!);

        // Assert
        context.Response.Body.Position = 0;
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Equal(largePayload.Length, output.Length);
        Assert.Contains("alice@example.com", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(true, context.Items["BotDetection.ResponsePiiMasking.Attempted"]);
        Assert.Equal(true, context.Items["BotDetection.ResponsePiiMasking.FailOpen"]);
    }

    [Fact]
    public async Task InvokeAsync_HighConfidenceMalicious_AutoAppliesMaskPii()
    {
        // Arrange
        const string responseBody = "{\"email\":\"alice@example.com\"}";
        RequestDelegate next = async context =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(responseBody);
        };

        var middleware = new BotDetectionMiddleware(
            next,
            Mock.Of<ILogger<BotDetectionMiddleware>>(),
            Options.Create(new BotDetectionOptions
            {
                DefaultActionPolicyName = null,
                ResponsePiiMasking = new ResponsePiiMaskingOptions
                {
                    Enabled = true
                }
            }));

        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.9,
            Confidence = 0.95,
            RiskBand = RiskBand.High,
            PrimaryBotType = BotType.MaliciousBot,
            Signals = new Dictionary<string, object>(),
            ContributingDetectors = new HashSet<string>()
        };

        var mockOrchestrator = CreateMockOrchestrator(evidence);
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = new Mock<IActionPolicyRegistry>();
        mockActionPolicyRegistry
            .Setup(r => r.GetPolicy(It.IsAny<string>()))
            .Returns((IActionPolicy?)null);

        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            },
            RequestServices = new ServiceCollection()
                .AddSingleton<IResponsePiiMasker, MicrosoftRecognizersResponsePiiMasker>()
                .BuildServiceProvider()
        };

        // Act
        await middleware.InvokeAsync(
            context,
            mockOrchestrator.Object,
            mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object,
            null!);

        // Assert
        context.Response.Body.Position = 0;
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Contains("[REDACTED:PII]", output, StringComparison.Ordinal);
        Assert.Equal("mask-pii", context.Items["BotDetection.Action"]);
    }

    [Fact]
    public async Task InvokeAsync_LogOnlyBlockPath_AppliesMaskPiiMutation()
    {
        // Arrange
        const string responseBody = "{\"email\":\"alice@example.com\"}";
        RequestDelegate next = async context =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(responseBody);
        };

        var middleware = new BotDetectionMiddleware(
            next,
            Mock.Of<ILogger<BotDetectionMiddleware>>(),
            Options.Create(new BotDetectionOptions
            {
                BotThreshold = 0.7,
                ResponsePiiMasking = new ResponsePiiMaskingOptions
                {
                    Enabled = true
                }
            }));

        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.96,
            Confidence = 0.95,
            RiskBand = RiskBand.VeryHigh,
            PrimaryBotType = BotType.MaliciousBot,
            Signals = new Dictionary<string, object>(),
            ContributingDetectors = new HashSet<string>()
        };

        var mockOrchestrator = CreateMockOrchestrator(evidence);
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = new Mock<IActionPolicyRegistry>();
        mockActionPolicyRegistry
            .Setup(r => r.GetPolicy(It.IsAny<string>()))
            .Returns((IActionPolicy?)null);

        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            },
            RequestServices = new ServiceCollection()
                .AddSingleton<IResponsePiiMasker, MicrosoftRecognizersResponsePiiMasker>()
                .BuildServiceProvider()
        };
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new BotPolicyAttribute("default")
            {
                BlockAction = BotBlockAction.LogOnly
            }),
            "logonly-policy"));

        // Act
        await middleware.InvokeAsync(
            context,
            mockOrchestrator.Object,
            mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object,
            null!);

        // Assert
        context.Response.Body.Position = 0;
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Contains("[REDACTED:PII]", output, StringComparison.Ordinal);
        Assert.Equal("mask-pii", context.Items["BotDetection.Action"]);
        Assert.Equal(true, context.Items["BotDetection.ResponsePiiMasking.Attempted"]);
    }

    [Fact]
    public async Task InvokeAsync_MaskPiiAction_FeatureDisabled_DoesNotMutateResponse()
    {
        // Arrange
        const string responseBody = "{\"email\":\"alice@example.com\"}";
        RequestDelegate next = async context =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(responseBody);
        };

        var middleware = new BotDetectionMiddleware(
            next,
            Mock.Of<ILogger<BotDetectionMiddleware>>(),
            Options.Create(new BotDetectionOptions { BotThreshold = 0.7 }));

        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.99,
            Confidence = 0.99,
            RiskBand = RiskBand.VeryHigh,
            TriggeredActionPolicyName = "mask-pii",
            Signals = new Dictionary<string, object>(),
            ContributingDetectors = new HashSet<string>()
        };

        var mockOrchestrator = CreateMockOrchestrator(evidence);
        var mockPolicyRegistry = CreateMockPolicyRegistry();
        var mockActionPolicyRegistry = new Mock<IActionPolicyRegistry>();
        mockActionPolicyRegistry
            .Setup(r => r.GetPolicy("mask-pii"))
            .Returns(new MarkerActionPolicy("mask-pii", "mask-pii"));

        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            },
            RequestServices = new ServiceCollection()
                .AddSingleton<IResponsePiiMasker, MicrosoftRecognizersResponsePiiMasker>()
                .BuildServiceProvider()
        };

        // Act
        await middleware.InvokeAsync(
            context,
            mockOrchestrator.Object,
            mockPolicyRegistry.Object,
            mockActionPolicyRegistry.Object,
            null!);

        // Assert
        context.Response.Body.Position = 0;
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Contains("alice@example.com", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(false, context.Items["BotDetection.ResponsePiiMasking.Attempted"]);
        Assert.Equal(true, context.Items["BotDetection.ResponsePiiMasking.Skipped"]);
        Assert.Equal("feature-disabled", context.Items["BotDetection.ResponsePiiMasking.SkipReason"]);
    }

    private sealed class MarkerActionPolicy(string name, string marker) : IActionPolicy
    {
        public string Name { get; } = name;
        public ActionType ActionType => ActionType.LogOnly;

        public Task<ActionResult> ExecuteAsync(
            HttpContext context,
            AggregatedEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            context.Items["BotDetection.Action"] = marker;
            return Task.FromResult(ActionResult.Allowed("marker"));
        }
    }
}
