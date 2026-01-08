using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit.Abstractions;
// Resolve ambiguous RiskBand reference

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Integration tests for the complete learning feedback loop.
///     Uses Microsoft.Extensions.Logging.Testing for log verification.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Learning")]
public class LearningFeedbackLoopTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private string _dbPath = null!;

    public LearningFeedbackLoopTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        // Create unique temp database for each test
        _dbPath = Path.Combine(Path.GetTempPath(), $"botdetection_test_{Guid.NewGuid():N}.db");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // Clean up test database
        if (File.Exists(_dbPath))
            try
            {
                File.Delete(_dbPath);
            }
            catch
            {
                /* ignore */
            }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task WeightStore_UpsertAndRetrieve_WorksCorrectly()
    {
        // Arrange
        var collector = new FakeLogCollector();
        var logger = new FakeLogger<SqliteWeightStore>(collector);
        var options = Options.Create(new BotDetectionOptions { DatabasePath = _dbPath });
        var store = new SqliteWeightStore(logger, options);

        // Act
        await store.UpdateWeightAsync(
            SignatureTypes.UaPattern,
            "chrome:win:len2",
            0.75,
            0.8,
            10,
            CancellationToken.None);

        var weight = await store.GetWeightAsync(
            SignatureTypes.UaPattern,
            "chrome:win:len2",
            CancellationToken.None);

        // Assert
        _output.WriteLine($"Retrieved weight: {weight:F3} (expected: 0.60 = 0.75 * 0.8)");
        Assert.Equal(0.6, weight, 0.01); // weight * confidence

        // Verify logging
        var logs = collector.GetSnapshot();
        Assert.Contains(logs, l => l.Message.Contains("Updated weight"));
    }

    [Fact]
    public async Task WeightStore_RecordObservation_UpdatesWithEMA()
    {
        // Arrange
        var collector = new FakeLogCollector();
        var logger = new FakeLogger<SqliteWeightStore>(collector);
        var options = Options.Create(new BotDetectionOptions { DatabasePath = _dbPath });
        var store = new SqliteWeightStore(logger, options);

        // Act - Record multiple observations
        for (var i = 0; i < 10; i++)
            await store.RecordObservationAsync(
                SignatureTypes.UaPattern,
                "test-pattern",
                true,
                0.9,
                CancellationToken.None);

        var weight = await store.GetWeightAsync(
            SignatureTypes.UaPattern,
            "test-pattern",
            CancellationToken.None);

        // Assert
        _output.WriteLine($"Weight after 10 bot observations: {weight:F3}");
        Assert.True(weight > 0, "Weight should be positive after bot observations");

        var stats = await store.GetStatsAsync(CancellationToken.None);
        _output.WriteLine($"Store stats: Total={stats.TotalWeights}, UA={stats.UaPatternWeights}");
        Assert.Equal(1, stats.TotalWeights);
        Assert.Equal(1, stats.UaPatternWeights);
    }

    [Fact]
    public async Task WeightStore_GetStats_ReturnsCorrectBreakdown()
    {
        // Arrange
        var collector = new FakeLogCollector();
        var logger = new FakeLogger<SqliteWeightStore>(collector);
        var options = Options.Create(new BotDetectionOptions { DatabasePath = _dbPath });
        var store = new SqliteWeightStore(logger, options);

        // Add weights of different types
        await store.UpdateWeightAsync(SignatureTypes.UaPattern, "ua1", 0.5, 0.8, 5);
        await store.UpdateWeightAsync(SignatureTypes.UaPattern, "ua2", 0.6, 0.7, 3);
        await store.UpdateWeightAsync(SignatureTypes.IpRange, "ip1", 0.4, 0.9, 10);
        await store.UpdateWeightAsync(SignatureTypes.PathPattern, "path1", 0.7, 0.6, 2);
        await store.UpdateWeightAsync(SignatureTypes.BehaviorHash, "hash1", 0.8, 0.85, 15);

        // Act
        var stats = await store.GetStatsAsync(CancellationToken.None);

        // Assert
        _output.WriteLine(
            $"Stats: Total={stats.TotalWeights}, UA={stats.UaPatternWeights}, IP={stats.IpRangeWeights}, " +
            $"Path={stats.PathPatternWeights}, Behavior={stats.BehaviorHashWeights}");

        Assert.Equal(5, stats.TotalWeights);
        Assert.Equal(2, stats.UaPatternWeights);
        Assert.Equal(1, stats.IpRangeWeights);
        Assert.Equal(1, stats.PathPatternWeights);
        Assert.Equal(1, stats.BehaviorHashWeights);
    }

    [Fact]
    public async Task SignatureFeedbackHandler_ProcessesHighConfidenceDetection()
    {
        // Arrange
        var collector = new FakeLogCollector();
        var handlerLogger = new FakeLogger<SignatureFeedbackHandler>(collector);
        var storeLogger = new FakeLogger<SqliteWeightStore>(new FakeLogCollector());
        var options = Options.Create(new BotDetectionOptions { DatabasePath = _dbPath });
        var store = new SqliteWeightStore(storeLogger, options);
        var handler = new SignatureFeedbackHandler(handlerLogger, store, options);

        var evt = new LearningEvent
        {
            Type = LearningEventType.HighConfidenceDetection,
            Source = "Test",
            Confidence = 0.95,
            Label = true,
            RequestId = "test-123",
            Metadata = new Dictionary<string, object>
            {
                ["userAgent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0",
                ["ip"] = "192.168.1.100",
                ["path"] = "/api/users/123"
            }
        };

        // Act
        await handler.HandleAsync(evt, CancellationToken.None);

        // Assert - Check that weights were recorded
        var stats = await store.GetStatsAsync(CancellationToken.None);
        _output.WriteLine($"After high-confidence detection: {stats.TotalWeights} weights recorded");

        // Should have UA pattern, IP range, path pattern, and combined signature
        Assert.True(stats.TotalWeights >= 3, $"Expected at least 3 weights, got {stats.TotalWeights}");

        // Verify logging
        var logs = collector.GetSnapshot();
        Assert.Contains(logs, l => l.Message.Contains("High-confidence detection"));
    }

    [Fact]
    public async Task SignatureFeedbackHandler_IgnoresLowConfidenceEvents()
    {
        // Arrange
        var collector = new FakeLogCollector();
        var handlerLogger = new FakeLogger<SignatureFeedbackHandler>(collector);
        var storeLogger = new FakeLogger<SqliteWeightStore>(new FakeLogCollector());
        var options = Options.Create(new BotDetectionOptions { DatabasePath = _dbPath });
        var store = new SqliteWeightStore(storeLogger, options);
        var handler = new SignatureFeedbackHandler(handlerLogger, store, options);

        var evt = new LearningEvent
        {
            Type = LearningEventType.HighConfidenceDetection,
            Source = "Test",
            Confidence = 0.5, // Below threshold
            Label = true,
            RequestId = "test-low"
        };

        // Act
        await handler.HandleAsync(evt, CancellationToken.None);

        // Assert - No weights should be recorded
        var stats = await store.GetStatsAsync(CancellationToken.None);
        Assert.Equal(0, stats.TotalWeights);
    }

    [Fact]
    public async Task HeuristicFeatureExtractor_ProducesFixedSizeVector()
    {
        // Arrange
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        // Create mock aggregated evidence with ledger
        var ledger = new DetectionLedger("test-request");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "User-Agent Detector",
            Category = "UserAgent",
            ConfidenceDelta = 0.8,
            Weight = 1.0,
            Reason = "Known bot pattern"
        });
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Header Detector",
            Category = "Headers",
            ConfidenceDelta = 0.3,
            Weight = 0.8,
            Reason = "Missing Accept-Language"
        });
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Version Age Detector",
            Category = "Version",
            ConfidenceDelta = 0.5,
            Weight = 0.6,
            Reason = "Outdated browser version"
        });

        var evidence = new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = 0.75,
            Confidence = 0.85,
            RiskBand = RiskBand.High,
            CategoryBreakdown = new Dictionary<string, CategoryScore>
            {
                ["UserAgent"] = new()
                {
                    Category = "UserAgent", Score = 0.8, TotalWeight = 1.0, ContributionCount = 1, Reasons = ["Known bot"]
                },
                ["Headers"] = new()
                {
                    Category = "Headers", Score = 0.3, TotalWeight = 0.8, ContributionCount = 1,
                    Reasons = ["Missing headers"]
                },
                ["Version"] = new()
                    { Category = "Version", Score = 0.5, TotalWeight = 0.6, ContributionCount = 1, Reasons = ["Outdated"] }
            },
            Signals = ImmutableDictionary<string, object>.Empty,
            ContributingDetectors = new HashSet<string>
                { "User-Agent Detector", "Header Detector", "Version Age Detector" },
            FailedDetectors = new HashSet<string>()
        };

        // Create a mock HttpContext
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "TestBot/1.0 (compatible; Test)";
        context.Request.Headers["Accept-Language"] = "en-US";
        context.Request.Path = "/api/test";

        // Act
        var features = HeuristicFeatureExtractor.ExtractFeatures(context, evidence);

        // Assert - features is now Dictionary<string, float>
        _output.WriteLine($"Feature count: {features.Count}");
        Assert.True(features.Count > 0, "Should extract at least some features");

        // Log key features by prefix
        foreach (var (key, value) in features.OrderBy(f => f.Key).Take(20)) _output.WriteLine($"{key}: {value:F3}");

        // Verify detector scores are present (dynamic keys based on detector names)
        Assert.True(features.ContainsKey("det:user-agent detector"), "Should have UA detector");
        Assert.True(features.ContainsKey("det:header detector"), "Should have Header detector");
        Assert.True(features.ContainsKey("det:version age detector"), "Should have Version detector");

        // Verify category scores
        Assert.True(features.ContainsKey("cat:useragent"), "Should have UserAgent category");
        Assert.True(features.ContainsKey("cat:headers"), "Should have Headers category");

        // Verify result signals are present
        Assert.True(features.ContainsKey("result:bot_probability"), "Should have bot probability");
        Assert.True(features.ContainsKey("result:confidence"), "Should have confidence");
    }

    [Fact]
    public async Task HeuristicFeatureExtractor_ExtractsMinimalEvidence()
    {
        // Arrange
        using var host = await CreateTestHost();

        // Create minimal evidence with just one detector
        var ledger2 = new DetectionLedger("test-request-2");
        ledger2.AddContribution(new DetectionContribution
        {
            DetectorName = "Single Detector",
            Category = "Test",
            ConfidenceDelta = 0.6,
            Weight = 1.0,
            Reason = "Test reason"
        });

        var evidence = new AggregatedEvidence
        {
            Ledger = ledger2,
            BotProbability = 0.4,
            Confidence = 0.6,
            RiskBand = RiskBand.Medium,
            CategoryBreakdown = new Dictionary<string, CategoryScore>
            {
                ["Test"] = new()
                    { Category = "Test", Score = 0.6, TotalWeight = 1.0, ContributionCount = 1, Reasons = ["Test"] }
            },
            Signals = ImmutableDictionary<string, object>.Empty,
            ContributingDetectors = new HashSet<string> { "Single Detector" },
            FailedDetectors = new HashSet<string>()
        };

        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "Test";

        // Act
        var features = HeuristicFeatureExtractor.ExtractFeatures(context, evidence);

        // Assert - dynamic dictionary should contain expected keys
        Assert.True(features.Count > 0, "Should extract features");

        // Single detector should be present
        Assert.True(features.ContainsKey("det:single detector"), "Should have single detector");
        Assert.Equal(0.6f, features["det:single detector"], 0.01);

        // Category should be present
        Assert.True(features.ContainsKey("cat:test"), "Should have test category");

        // Bot probability and confidence should be present
        Assert.True(features.ContainsKey("result:bot_probability"), "Should have bot probability");
        Assert.Equal(0.4f, features["result:bot_probability"], 0.01);

        _output.WriteLine($"Extracted {features.Count} features from minimal evidence");
        foreach (var (key, value) in features.OrderBy(f => f.Key)) _output.WriteLine($"  {key}: {value:F3}");
    }

    [Fact]
    public async Task LearningEventBus_PublishAndConsume_WorksEndToEnd()
    {
        // Arrange
        var collector = new FakeLogCollector();
        var bus = new LearningEventBus(100);
        var receivedEvents = new List<LearningEvent>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start consumer
        var consumerTask = Task.Run(async () =>
        {
            await foreach (var evt in bus.Reader.ReadAllAsync(cts.Token))
            {
                receivedEvents.Add(evt);
                if (receivedEvents.Count >= 5)
                    break;
            }
        }, cts.Token);

        // Act - Publish events
        for (var i = 0; i < 5; i++)
        {
            var published = bus.TryPublish(new LearningEvent
            {
                Type = LearningEventType.HighConfidenceDetection,
                Source = "Test",
                Confidence = 0.9 + i * 0.01,
                RequestId = $"req-{i}"
            });
            Assert.True(published, $"Failed to publish event {i}");
        }

        // Wait for consumer
        await consumerTask;

        // Assert
        Assert.Equal(5, receivedEvents.Count);
        _output.WriteLine($"Received {receivedEvents.Count} events");

        for (var i = 0; i < 5; i++) Assert.Equal($"req-{i}", receivedEvents[i].RequestId);
    }

    [Fact]
    public async Task FullIntegration_DetectionToLearning_WorksEndToEnd()
    {
        // Arrange
        var collector = new FakeLogCollector();
        using var host = await CreateTestHostWithLearning(collector);

        var weightStore = host.Services.GetRequiredService<IWeightStore>();
        var eventBus = host.Services.GetRequiredService<ILearningEventBus>();

        // Act - Simulate a high-confidence detection
        var evt = new LearningEvent
        {
            Type = LearningEventType.HighConfidenceDetection,
            Source = "IntegrationTest",
            Confidence = 0.92,
            Label = true,
            RequestId = "integration-test-1",
            Metadata = new Dictionary<string, object>
            {
                ["userAgent"] = "ScrapingBot/2.0 (http://scraper.example.com)",
                ["ip"] = "10.0.0.50",
                ["path"] = "/api/data"
            }
        };

        eventBus.TryPublish(evt);

        // Give the background service time to process
        await Task.Delay(500);

        // Assert - Check weights were recorded
        var stats = await weightStore.GetStatsAsync(CancellationToken.None);
        _output.WriteLine("Weight store stats after detection:");
        _output.WriteLine($"  Total: {stats.TotalWeights}");
        _output.WriteLine($"  UA Patterns: {stats.UaPatternWeights}");
        _output.WriteLine($"  IP Ranges: {stats.IpRangeWeights}");
        _output.WriteLine($"  Path Patterns: {stats.PathPatternWeights}");

        // Verify logs
        var logs = collector.GetSnapshot();
        _output.WriteLine($"\nCaptured {logs.Count} log entries");
        foreach (var log in logs.Where(l => l.Level >= LogLevel.Information).Take(10))
            _output.WriteLine($"  [{log.Level}] {log.Message}");

        // Should have recorded weights
        Assert.True(stats.TotalWeights > 0, "Expected weights to be recorded");
    }

    [Fact]
    public async Task VersionAgeDetector_DetectsOutdatedBrowser()
    {
        // Arrange
        var collector = new FakeLogCollector();
        var detectorLogger = new FakeLogger<VersionAgeDetector>(collector);
        var versionServiceLogger = new FakeLogger<BrowserVersionService>(new FakeLogCollector());

        var options = Options.Create(new BotDetectionOptions
        {
            VersionAge = new VersionAgeOptions
            {
                Enabled = true,
                MaxBrowserVersionAge = 10,
                FallbackBrowserVersions = new Dictionary<string, int>
                {
                    ["Chrome"] = 131,
                    ["Firefox"] = 133
                }
            }
        });

        var httpClientFactory = new TestHttpClientFactory();
        var versionService = new BrowserVersionService(versionServiceLogger, options, httpClientFactory);

        var detector = new VersionAgeDetector(detectorLogger, options, versionService);

        // Create context with outdated Chrome
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.0.0 Safari/537.36";

        // Act
        var result = await detector.DetectAsync(context, CancellationToken.None);

        // Assert
        _output.WriteLine($"Detection result: Confidence={result.Confidence:F3}");
        foreach (var reason in result.Reasons)
            _output.WriteLine($"  Reason: {reason.Category} - {reason.Detail} (+{reason.ConfidenceImpact:F2})");

        // Chrome 90 is severely outdated (41 versions behind Chrome 131)
        Assert.True(result.Confidence > 0, "Should detect outdated browser");
        Assert.Contains(result.Reasons, r => r.Category == "BrowserVersion");

        // Verify logging
        var logs = collector.GetSnapshot();
        Assert.Contains(logs, l => l.Message.Contains("outdated") || l.Message.Contains("behind"));
    }

    [Fact]
    public async Task VersionAgeDetector_DetectsImpossibleCombination()
    {
        // Arrange
        var collector = new FakeLogCollector();
        var detectorLogger = new FakeLogger<VersionAgeDetector>(collector);
        var versionServiceLogger = new FakeLogger<BrowserVersionService>(new FakeLogCollector());

        var options = Options.Create(new BotDetectionOptions
        {
            VersionAge = new VersionAgeOptions
            {
                Enabled = true,
                MinBrowserVersionByOs = new Dictionary<string, int>
                {
                    ["Windows NT 5"] = 49 // Chrome stopped at 49 on XP
                },
                ImpossibleCombinationConfidence = 0.5
            }
        });

        var httpClientFactory = new TestHttpClientFactory();
        var versionService = new BrowserVersionService(versionServiceLogger, options, httpClientFactory);

        var detector = new VersionAgeDetector(detectorLogger, options, versionService);

        // Create context with impossible combination: Chrome 131 on Windows XP
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 5.1; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        // Act
        var result = await detector.DetectAsync(context, CancellationToken.None);

        // Assert
        _output.WriteLine($"Detection result: Confidence={result.Confidence:F3}");
        foreach (var reason in result.Reasons) _output.WriteLine($"  Reason: {reason.Category} - {reason.Detail}");

        Assert.True(result.Confidence >= 0.5, "Should detect impossible combination");
        Assert.Contains(result.Reasons, r => r.Category == "ImpossibleCombination");
        Assert.Equal(BotType.Scraper, result.BotType);

        // Verify logging
        var logs = collector.GetSnapshot();
        Assert.Contains(logs, l => l.Message.Contains("Impossible"));
    }

    [Fact]
    public async Task WeightStore_DecayOldWeights_ReducesConfidence()
    {
        // Arrange
        var collector = new FakeLogCollector();
        var logger = new FakeLogger<SqliteWeightStore>(collector);
        var options = Options.Create(new BotDetectionOptions { DatabasePath = _dbPath });
        var store = new SqliteWeightStore(logger, options);

        // Add a weight
        await store.UpdateWeightAsync(SignatureTypes.UaPattern, "old-pattern", 0.9, 0.95, 100);

        // Verify initial state
        var initialWeight = await store.GetWeightAsync(SignatureTypes.UaPattern, "old-pattern");
        _output.WriteLine($"Initial weight: {initialWeight:F3}");
        Assert.True(initialWeight > 0.8, "Initial weight should be high");

        // Act - Decay with 0.5 factor
        await store.DecayOldWeightsAsync(TimeSpan.Zero, 0.5, CancellationToken.None);

        // Assert
        var decayedWeight = await store.GetWeightAsync(SignatureTypes.UaPattern, "old-pattern");
        _output.WriteLine($"Decayed weight: {decayedWeight:F3}");

        // Weight should be reduced (weight * confidence * decay^2)
        Assert.True(decayedWeight < initialWeight, "Weight should be reduced after decay");

        // Verify logging
        var logs = collector.GetSnapshot();
        Assert.Contains(logs, l => l.Message.Contains("decay"));
    }

    [Fact]
    public async Task MinimalConfig_WorksWithDefaults()
    {
        // This is a critical test - minimal configuration MUST work
        var collector = new FakeLogCollector();

        using var host = await new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddFakeLogging(collector);
            })
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        // Minimal config - just call AddBotDetection with no options
                        services.AddBotDetection();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseBotDetection();
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();

        // Act - Make a simple request
        var response = await client.GetAsync("/");

        // Assert - Should not throw, should return some response
        _output.WriteLine($"Response status: {response.StatusCode}");

        // Verify critical services are registered
        var botDetectionService = host.Services.GetService<IBotDetectionService>();
        var weightStore = host.Services.GetService<IWeightStore>();
        var versionService = host.Services.GetService<IBrowserVersionService>();
        var eventBus = host.Services.GetService<ILearningEventBus>();

        Assert.NotNull(botDetectionService);
        Assert.NotNull(weightStore);
        Assert.NotNull(versionService);
        Assert.NotNull(eventBus);

        _output.WriteLine("All critical services registered successfully");

        // Check logs for any errors
        var logs = collector.GetSnapshot();
        var errors = logs.Where(l => l.Level >= LogLevel.Error).ToList();

        foreach (var error in errors) _output.WriteLine($"ERROR: {error.Message}");

        Assert.Empty(errors); // No errors during startup

        _output.WriteLine($"Minimal config test passed with {logs.Count} log entries");
    }

    [Fact]
    public async Task SimpleBotDetection_WorksWithMinimalOverhead()
    {
        // Test the AddSimpleBotDetection() path
        var collector = new FakeLogCollector();

        using var host = await new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddFakeLogging(collector);
            })
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSimpleBotDetection();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseBotDetection();
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();

        // Act - Make request with bot user agent
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.UserAgent.ParseAdd("Googlebot/2.1 (+http://www.google.com/bot.html)");

        var response = await client.SendAsync(request);

        // Assert
        _output.WriteLine($"Bot request status: {response.StatusCode}");

        var logs = collector.GetSnapshot();
        var errors = logs.Where(l => l.Level >= LogLevel.Error).ToList();

        Assert.Empty(errors);

        _output.WriteLine("Simple bot detection test passed");
    }

    private async Task<IHost> CreateTestHost()
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddBotDetection(options => { options.DatabasePath = _dbPath; });
                    })
                    .Configure(app => { app.UseRouting(); });
            })
            .StartAsync();

        return host;
    }

    private async Task<IHost> CreateTestHostWithLearning(FakeLogCollector collector)
    {
        var host = await new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddFakeLogging(collector);
                logging.SetMinimumLevel(LogLevel.Debug);
            })
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddBotDetection(options => { options.DatabasePath = _dbPath; });
                    })
                    .Configure(app => { app.UseRouting(); });
            })
            .StartAsync();

        return host;
    }
}

/// <summary>
///     Test HTTP client factory that returns a basic client.
/// </summary>
public class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient();
    }
}

/// <summary>
///     Extension methods for adding fake logging to test hosts.
/// </summary>
public static class FakeLoggingExtensions
{
    public static ILoggingBuilder AddFakeLogging(this ILoggingBuilder builder, FakeLogCollector collector)
    {
        builder.Services.AddSingleton(collector);
        builder.Services.AddSingleton<ILoggerProvider>(sp => new FakeLoggerProvider(collector));
        return builder;
    }
}

/// <summary>
///     Fake logger provider for testing.
/// </summary>
public class FakeLoggerProvider : ILoggerProvider
{
    private readonly FakeLogCollector _collector;

    public FakeLoggerProvider(FakeLogCollector collector)
    {
        _collector = collector;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FakeLogger<object>(_collector);
    }

    public void Dispose()
    {
    }
}