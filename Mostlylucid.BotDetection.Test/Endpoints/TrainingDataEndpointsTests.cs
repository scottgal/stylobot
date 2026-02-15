using Mostlylucid.BotDetection.Endpoints;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Endpoints;

/// <summary>
///     Tests for <see cref="TrainingDataEndpoints" />.
///     Tests FilterPiiSignals, GeneralizePath, label derivation, derived features, and config options.
/// </summary>
public class TrainingDataEndpointsTests
{
    #region FilterPiiSignals — Null / Empty

    [Fact]
    public void FilterPiiSignals_Null_ReturnsNull()
    {
        var result = TrainingDataEndpoints.FilterPiiSignals(null);
        Assert.Null(result);
    }

    [Fact]
    public void FilterPiiSignals_Empty_ReturnsNull()
    {
        var signals = new Dictionary<string, object>();
        var result = TrainingDataEndpoints.FilterPiiSignals(signals);
        Assert.Null(result);
    }

    #endregion

    #region FilterPiiSignals — PII Removal

    [Fact]
    public void FilterPiiSignals_RemovesUserAgent()
    {
        var signals = new Dictionary<string, object>
        {
            { SignalKeys.UserAgent, "Mozilla/5.0" },
            { "detection.score", 0.85 }
        };

        var result = TrainingDataEndpoints.FilterPiiSignals(signals);

        Assert.NotNull(result);
        Assert.DoesNotContain(SignalKeys.UserAgent, result!.Keys);
        Assert.Contains("detection.score", result.Keys);
    }

    [Fact]
    public void FilterPiiSignals_RemovesClientIp()
    {
        var signals = new Dictionary<string, object>
        {
            { SignalKeys.ClientIp, "192.168.1.1" },
            { "geo.country_code", "US" }
        };

        var result = TrainingDataEndpoints.FilterPiiSignals(signals);

        Assert.NotNull(result);
        Assert.DoesNotContain(SignalKeys.ClientIp, result!.Keys);
        Assert.Contains("geo.country_code", result.Keys);
    }

    [Fact]
    public void FilterPiiSignals_KeepsNonPiiSignals()
    {
        var signals = new Dictionary<string, object>
        {
            { "detection.score", 0.85 },
            { "geo.country_code", "US" },
            { "request.timing", 42.0 }
        };

        var result = TrainingDataEndpoints.FilterPiiSignals(signals);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
    }

    [Fact]
    public void FilterPiiSignals_AllPii_ReturnsNull()
    {
        var signals = new Dictionary<string, object>
        {
            { SignalKeys.UserAgent, "Mozilla/5.0" },
            { SignalKeys.ClientIp, "10.0.0.1" }
        };

        var result = TrainingDataEndpoints.FilterPiiSignals(signals);

        Assert.Null(result);
    }

    #endregion

    #region FilterPiiSignals — Conditional UA for Bots

    [Fact]
    public void FilterPiiSignals_HumanVisitor_StripsUaClassificationKeys()
    {
        var signals = new Dictionary<string, object>
        {
            { SignalKeys.UserAgentIsBot, false },
            { SignalKeys.UserAgentBotType, "none" },
            { SignalKeys.UserAgentBotName, "" },
            { SignalKeys.UserAgentOs, "Windows 10" },
            { SignalKeys.UserAgentBrowser, "Chrome 120" },
            { "detection.score", 0.15 }
        };

        var result = TrainingDataEndpoints.FilterPiiSignals(signals, isBotDetected: false);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Contains("detection.score", result.Keys);
        Assert.DoesNotContain(SignalKeys.UserAgentIsBot, result.Keys);
        Assert.DoesNotContain(SignalKeys.UserAgentBotType, result.Keys);
        Assert.DoesNotContain(SignalKeys.UserAgentBotName, result.Keys);
        Assert.DoesNotContain(SignalKeys.UserAgentOs, result.Keys);
        Assert.DoesNotContain(SignalKeys.UserAgentBrowser, result.Keys);
    }

    [Fact]
    public void FilterPiiSignals_BotDetected_KeepsUaClassificationKeys()
    {
        var signals = new Dictionary<string, object>
        {
            { SignalKeys.UserAgentIsBot, true },
            { SignalKeys.UserAgentBotType, "crawler" },
            { SignalKeys.UserAgentBotName, "Googlebot" },
            { SignalKeys.UserAgentOs, "Linux" },
            { SignalKeys.UserAgentBrowser, "Googlebot/2.1" },
            { SignalKeys.UserAgent, "Mozilla/5.0 (compatible; Googlebot/2.1)" },
            { "detection.score", 0.92 }
        };

        var result = TrainingDataEndpoints.FilterPiiSignals(signals, isBotDetected: true);

        Assert.NotNull(result);
        // ua.raw still stripped even for bots
        Assert.DoesNotContain(SignalKeys.UserAgent, result!.Keys);
        // UA classification keys kept for bots
        Assert.Contains(SignalKeys.UserAgentIsBot, result.Keys);
        Assert.Contains(SignalKeys.UserAgentBotType, result.Keys);
        Assert.Contains(SignalKeys.UserAgentBotName, result.Keys);
        Assert.Contains(SignalKeys.UserAgentOs, result.Keys);
        Assert.Contains(SignalKeys.UserAgentBrowser, result.Keys);
        Assert.Contains("detection.score", result.Keys);
    }

    [Fact]
    public void FilterPiiSignals_BotDetected_AlwaysStripsRawUaAndIp()
    {
        var signals = new Dictionary<string, object>
        {
            { SignalKeys.UserAgent, "Scrapy/2.11" },
            { SignalKeys.ClientIp, "203.0.113.50" },
            { SignalKeys.UserAgentIsBot, true },
            { "detection.score", 0.95 }
        };

        var result = TrainingDataEndpoints.FilterPiiSignals(signals, isBotDetected: true);

        Assert.NotNull(result);
        Assert.DoesNotContain(SignalKeys.UserAgent, result!.Keys);
        Assert.DoesNotContain(SignalKeys.ClientIp, result.Keys);
    }

    #endregion

    #region GeneralizePath

    [Fact]
    public void GeneralizePath_NullOrEmpty_ReturnsSlash()
    {
        Assert.Equal("/", TrainingDataEndpoints.GeneralizePath(null));
        Assert.Equal("/", TrainingDataEndpoints.GeneralizePath(""));
    }

    [Fact]
    public void GeneralizePath_StripsQueryString()
    {
        var result = TrainingDataEndpoints.GeneralizePath("/api/search?token=abc123&user=42");
        Assert.DoesNotContain("token", result);
        Assert.DoesNotContain("abc123", result);
        Assert.DoesNotContain("user=42", result);
    }

    [Fact]
    public void GeneralizePath_ReplacesGuidSegments()
    {
        var result = TrainingDataEndpoints.GeneralizePath("/api/users/550e8400-e29b-41d4-a716-446655440000/profile");
        Assert.Equal("/api/users/*/profile", result);
    }

    [Fact]
    public void GeneralizePath_ReplacesLongNumericSegments()
    {
        var result = TrainingDataEndpoints.GeneralizePath("/api/orders/123456/items");
        Assert.Equal("/api/orders/*/items", result);
    }

    [Fact]
    public void GeneralizePath_ReplacesBase64Segments()
    {
        var result = TrainingDataEndpoints.GeneralizePath("/api/verify/eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9/status");
        Assert.Equal("/api/verify/*/status", result);
    }

    [Fact]
    public void GeneralizePath_PreservesStaticPathSegments()
    {
        var result = TrainingDataEndpoints.GeneralizePath("/blog/2024/hello-world");
        Assert.Equal("/blog/*/hello-world", result);
    }

    [Fact]
    public void GeneralizePath_PreservesShortNumericSegments()
    {
        // Short numbers (1-3 digits) are kept as they're usually route params, not IDs
        var result = TrainingDataEndpoints.GeneralizePath("/api/v2/page/3");
        Assert.Equal("/api/v2/page/3", result);
    }

    [Fact]
    public void GeneralizePath_NoRawPathsInOutput()
    {
        // Verify no query strings, GUIDs, or long numeric IDs survive
        var paths = new[]
        {
            "/users/550e8400-e29b-41d4-a716-446655440000",
            "/api/tokens/eyJhbGciOiJSUzI1NiJ9/refresh?sid=abc",
            "/orders/9876543210/receipt"
        };

        foreach (var path in paths)
        {
            var result = TrainingDataEndpoints.GeneralizePath(path);
            Assert.DoesNotContain("?", result);
            Assert.DoesNotContain("550e8400", result);
            Assert.DoesNotContain("9876543210", result);
        }
    }

    #endregion

    #region Export Label Derivation

    [Theory]
    [InlineData(0.7, "bot")]
    [InlineData(0.85, "bot")]
    [InlineData(1.0, "bot")]
    public void DeriveLabel_HighProbability_Bot(double probability, string expectedLabel)
    {
        Assert.Equal(expectedLabel, TrainingDataEndpoints.DeriveLabel(probability));
    }

    [Theory]
    [InlineData(0.3, "human")]
    [InlineData(0.1, "human")]
    [InlineData(0.0, "human")]
    public void DeriveLabel_LowProbability_Human(double probability, string expectedLabel)
    {
        Assert.Equal(expectedLabel, TrainingDataEndpoints.DeriveLabel(probability));
    }

    [Theory]
    [InlineData(0.31, "uncertain")]
    [InlineData(0.5, "uncertain")]
    [InlineData(0.69, "uncertain")]
    public void DeriveLabel_MidProbability_Uncertain(double probability, string expectedLabel)
    {
        Assert.Equal(expectedLabel, TrainingDataEndpoints.DeriveLabel(probability));
    }

    #endregion

    #region ComputeDerived

    [Fact]
    public void ComputeDerived_SingleRequest_ZeroDurationAndRate()
    {
        var now = DateTime.UtcNow;
        var behavior = new SignatureBehavior
        {
            Signature = "test-sig",
            Requests = [new SignatureRequest { RequestId = "r1", Timestamp = now, Path = "/", BotProbability = 0.5, Signals = new Dictionary<string, object>(), DetectorsRan = [] }],
            FirstSeen = now,
            LastSeen = now,
            RequestCount = 1,
            AverageInterval = 0,
            PathEntropy = 0,
            TimingCoefficient = 0,
            AverageBotProbability = 0.5,
            AberrationScore = 0,
            IsAberrant = false
        };

        var d = TrainingDataEndpoints.ComputeDerived(behavior);

        Assert.Equal(0, d.DurationSeconds);
        Assert.Equal(0, d.RequestRate);
        Assert.Equal(1.0, d.PathDiversity); // 1 unique path / 1 request
        Assert.Equal(0, d.IntervalStdDev);
    }

    [Fact]
    public void ComputeDerived_MultipleRequests_CorrectRateAndDiversity()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var requests = new List<SignatureRequest>();
        for (var i = 0; i < 10; i++)
        {
            requests.Add(new SignatureRequest
            {
                RequestId = $"r{i}",
                Timestamp = start.AddSeconds(i * 6), // 6s apart
                Path = i % 2 == 0 ? "/page-a" : "/page-b",
                BotProbability = 0.8,
                Signals = new Dictionary<string, object>(),
                DetectorsRan = []
            });
        }

        var behavior = new SignatureBehavior
        {
            Signature = "multi-sig",
            Requests = requests,
            FirstSeen = start,
            LastSeen = start.AddSeconds(54), // 9 intervals * 6s
            RequestCount = 10,
            AverageInterval = 6.0,
            PathEntropy = 1.0,
            TimingCoefficient = 0.0,
            AverageBotProbability = 0.8,
            AberrationScore = 0,
            IsAberrant = false
        };

        var d = TrainingDataEndpoints.ComputeDerived(behavior);

        Assert.Equal(54.0, d.DurationSeconds);
        Assert.True(d.RequestRate > 0); // 10 requests in 54 seconds = ~11.1 rpm
        Assert.Equal(0.2, d.PathDiversity); // 2 unique / 10 total
        Assert.Equal(0, d.IntervalStdDev); // uniform intervals
    }

    [Fact]
    public void ComputeDerived_IrregularIntervals_NonZeroStdDev()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var requests = new List<SignatureRequest>
        {
            new() { RequestId = "r1", Timestamp = start, Path = "/a", BotProbability = 0.5, Signals = new Dictionary<string, object>(), DetectorsRan = [] },
            new() { RequestId = "r2", Timestamp = start.AddSeconds(1), Path = "/b", BotProbability = 0.5, Signals = new Dictionary<string, object>(), DetectorsRan = [] },
            new() { RequestId = "r3", Timestamp = start.AddSeconds(2), Path = "/c", BotProbability = 0.5, Signals = new Dictionary<string, object>(), DetectorsRan = [] },
            new() { RequestId = "r4", Timestamp = start.AddSeconds(100), Path = "/d", BotProbability = 0.5, Signals = new Dictionary<string, object>(), DetectorsRan = [] }
        };

        var behavior = new SignatureBehavior
        {
            Signature = "irregular-sig",
            Requests = requests,
            FirstSeen = start,
            LastSeen = start.AddSeconds(100),
            RequestCount = 4,
            AverageInterval = 33.3,
            PathEntropy = 2.0,
            TimingCoefficient = 1.5,
            AverageBotProbability = 0.5,
            AberrationScore = 0,
            IsAberrant = false
        };

        var d = TrainingDataEndpoints.ComputeDerived(behavior);

        Assert.True(d.IntervalStdDev > 0, "Irregular intervals should produce non-zero std dev");
    }

    #endregion

    #region TrainingEndpointsOptions Defaults

    [Fact]
    public void TrainingEndpointsOptions_DefaultValues()
    {
        var options = new TrainingEndpointsOptions();

        Assert.True(options.Enabled);
        Assert.False(options.RequireApiKey);
        Assert.Empty(options.ApiKeys);
        Assert.Equal(30, options.RateLimitPerMinute);
        Assert.Equal(10_000, options.MaxExportRecords);
    }

    [Fact]
    public void BotDetectionOptions_TrainingEndpointsProperty_IsNotNull()
    {
        var options = new BotDetectionOptions();
        Assert.NotNull(options.TrainingEndpoints);
    }

    #endregion
}
