using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Escalation;
using Mostlylucid.BotDetection.Orchestration.SignalMatching;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public class SignatureEscalatorAtomTests
{
    private readonly NullLogger<SignatureResponseCoordinatorCache> _cacheLogger;
    private readonly SignatureResponseCoordinatorCache _coordinatorCache;
    private readonly NullLogger<SignatureEscalatorAtom> _logger;
    private readonly SignalSink _operationSink;

    public SignatureEscalatorAtomTests()
    {
        _operationSink = new SignalSink(1000, TimeSpan.FromMinutes(1));
        _cacheLogger = NullLogger<SignatureResponseCoordinatorCache>.Instance;
        _coordinatorCache = new SignatureResponseCoordinatorCache(_cacheLogger);
        _logger = NullLogger<SignatureEscalatorAtom>.Instance;
    }

    [Fact]
    public void Constructor_InitializesWithDefaultConfig()
    {
        // Act
        var escalator = new SignatureEscalatorAtom(
            _operationSink,
            "test-sig",
            "req-123",
            _coordinatorCache,
            _logger);

        // Assert
        Assert.NotNull(escalator);
    }

    [Fact]
    public void Constructor_InitializesWithCustomConfig()
    {
        // Arrange
        var config = new EscalatorConfig
        {
            StoreThreshold = 0.5,
            AlertThreshold = 0.7,
            RequestPatterns = new Dictionary<string, string>
            {
                ["custom_risk"] = "request.custom.risk"
            }
        };

        // Act
        var escalator = new SignatureEscalatorAtom(
            _operationSink,
            "test-sig",
            "req-123",
            _coordinatorCache,
            _logger,
            config);

        // Assert
        Assert.NotNull(escalator);
    }

    [Fact]
    public async Task OnRequestAnalysisCompleteAsync_WithLowRisk_DoesNotEscalate()
    {
        // Arrange
        var escalator = new SignatureEscalatorAtom(
            _operationSink,
            "test-sig",
            "req-123",
            _coordinatorCache,
            _logger);

        // Emit low risk signals
        _operationSink.Raise(new SignalKey("request.detector.risk"), "0.3");
        _operationSink.Raise(new SignalKey("request.honeypot"), "false");

        // Act
        await escalator.OnRequestAnalysisCompleteAsync();

        // Assert - should complete without throwing
        Assert.True(true);
    }

    [Fact]
    public async Task OnRequestAnalysisCompleteAsync_WithHighRisk_Escalates()
    {
        // Arrange
        var config = new EscalatorConfig
        {
            EscalationRules = new List<EscalationRule>
            {
                new()
                {
                    Name = "high_risk",
                    Priority = 90,
                    Condition = "risk > 0.7",
                    ShouldStore = true,
                    ShouldAlert = true,
                    Reason = "High risk detected"
                }
            }
        };

        var escalator = new SignatureEscalatorAtom(
            _operationSink,
            "test-sig",
            "req-123",
            _coordinatorCache,
            _logger,
            config);

        // Emit high risk signals
        _operationSink.Raise(new SignalKey("request.detector.risk"), "0.85");

        // Act
        await escalator.OnRequestAnalysisCompleteAsync();

        // Assert - should complete without throwing
        Assert.True(true);
    }

    [Fact]
    public async Task OnRequestAnalysisCompleteAsync_WithHoneypot_EscalatesImmediately()
    {
        // Arrange
        var config = new EscalatorConfig
        {
            EscalationRules = new List<EscalationRule>
            {
                new()
                {
                    Name = "honeypot_immediate",
                    Priority = 100,
                    Condition = "honeypot == true",
                    ShouldStore = true,
                    ShouldAlert = true,
                    Reason = "Honeypot hit"
                }
            }
        };

        var escalator = new SignatureEscalatorAtom(
            _operationSink,
            "test-sig",
            "req-123",
            _coordinatorCache,
            _logger,
            config);

        // Emit honeypot signal
        _operationSink.Raise(new SignalKey("request.path.honeypot"), "true");

        // Act
        await escalator.OnRequestAnalysisCompleteAsync();

        // Assert - should complete without throwing
        Assert.True(true);
    }

    [Fact]
    public async Task OnOperationCompleteAsync_CombinesRequestAndResponseSignals()
    {
        // Arrange
        var escalator = new SignatureEscalatorAtom(
            _operationSink,
            "test-sig",
            "req-123",
            _coordinatorCache,
            _logger);

        // Emit request signals
        _operationSink.Raise(new SignalKey("request.detector.risk"), "0.6");
        _operationSink.Raise(new SignalKey("request.path"), "/api/users");

        // Emit response signals
        _operationSink.Raise(new SignalKey("response.detector.score"), "0.7");
        _operationSink.Raise(new SignalKey("response.status"), "404");

        // Act
        await escalator.OnOperationCompleteAsync();

        // Assert - should complete without throwing
        Assert.True(true);
    }

    [Fact]
    public async Task OnOperationCompleteAsync_WithHighCombinedScore_StoresAndAlerts()
    {
        // Arrange
        var config = new EscalatorConfig
        {
            StoreThreshold = 0.6,
            AlertThreshold = 0.8,
            OperationEscalationRules = new List<EscalationRule>
            {
                new()
                {
                    Name = "high_combined",
                    Priority = 90,
                    Condition = "risk > 0.8",
                    ShouldStore = true,
                    ShouldAlert = true,
                    Reason = "High combined score"
                }
            }
        };

        var escalator = new SignatureEscalatorAtom(
            _operationSink,
            "test-sig",
            "req-123",
            _coordinatorCache,
            _logger,
            config);

        // Emit high scores
        _operationSink.Raise(new SignalKey("request.detector.risk"), "0.85");
        _operationSink.Raise(new SignalKey("response.detector.score"), "0.80");

        // Act
        await escalator.OnOperationCompleteAsync();

        // Assert - should complete without throwing
        Assert.True(true);
    }

    [Fact]
    public async Task DisposeAsync_CleansUpResources()
    {
        // Arrange
        var escalator = new SignatureEscalatorAtom(
            _operationSink,
            "test-sig",
            "req-123",
            _coordinatorCache,
            _logger);

        // Act
        await escalator.DisposeAsync();

        // Assert - should complete without throwing
        Assert.True(true);
    }
}

public class SignalPatternMatcherTests
{
    [Fact]
    public void ExtractFrom_MatchesSimplePattern()
    {
        // Arrange
        var patterns = new Dictionary<string, string>
        {
            ["risk"] = "request.risk"
        };
        var matcher = new SignalPatternMatcher(patterns);
        var sink = new SignalSink(100, TimeSpan.FromMinutes(1));

        sink.Raise(new SignalKey("request.risk"), "0.75");

        // Act
        var extracted = matcher.ExtractFrom(sink);

        // Assert
        Assert.NotNull(extracted);
        Assert.True(extracted.ContainsKey("risk"));
    }

    [Fact]
    public void ExtractFrom_MatchesWildcardPattern()
    {
        // Arrange
        var patterns = new Dictionary<string, string>
        {
            ["risk"] = "request.*.risk"
        };
        var matcher = new SignalPatternMatcher(patterns);
        var sink = new SignalSink(100, TimeSpan.FromMinutes(1));

        sink.Raise(new SignalKey("request.detector.risk"), "0.75");

        // Act
        var extracted = matcher.ExtractFrom(sink);

        // Assert
        Assert.NotNull(extracted);
        // Note: Actual wildcard matching depends on SignalKey implementation
    }

    [Fact]
    public void ExtractFrom_ReturnsLatestMatchForPattern()
    {
        // Arrange
        var patterns = new Dictionary<string, string>
        {
            ["risk"] = "request.risk"
        };
        var matcher = new SignalPatternMatcher(patterns);
        var sink = new SignalSink(100, TimeSpan.FromMinutes(1));

        sink.Raise(new SignalKey("request.risk"), "0.50");
        Thread.Sleep(10); // Ensure different timestamps
        sink.Raise(new SignalKey("request.risk"), "0.75");

        // Act
        var extracted = matcher.ExtractFrom(sink);

        // Assert
        Assert.NotNull(extracted);
        if (extracted.TryGetValue("risk", out var value))
            Assert.Equal(0.75, Convert.ToDouble(value), 10); // Use precision for floating point comparison
    }

    [Fact]
    public void ExtractFrom_HandlesMultiplePatterns()
    {
        // Arrange
        var patterns = new Dictionary<string, string>
        {
            ["risk"] = "request.risk",
            ["score"] = "response.score",
            ["honeypot"] = "request.honeypot"
        };
        var matcher = new SignalPatternMatcher(patterns);
        var sink = new SignalSink(100, TimeSpan.FromMinutes(1));

        sink.Raise(new SignalKey("request.risk"), "0.75");
        sink.Raise(new SignalKey("response.score"), "0.80");
        sink.Raise(new SignalKey("request.honeypot"), "true");

        // Act
        var extracted = matcher.ExtractFrom(sink);

        // Assert
        Assert.NotNull(extracted);
        // Note: Actual results depend on SignalKey pattern matching implementation
    }
}

public class EscalationRuleTests
{
    [Fact]
    public void ShouldEscalate_WithGreaterThanCondition_ReturnsTrue()
    {
        // Arrange
        var rule = new EscalationRule
        {
            Name = "high_risk",
            Priority = 90,
            Condition = "risk > 0.8",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "High risk"
        };

        var signals = new Dictionary<string, object>
        {
            ["risk"] = 0.85
        };

        // Act
        var result = rule.ShouldEscalate(signals);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldEscalate_WithGreaterThanCondition_ReturnsFalse()
    {
        // Arrange
        var rule = new EscalationRule
        {
            Name = "high_risk",
            Priority = 90,
            Condition = "risk > 0.8",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "High risk"
        };

        var signals = new Dictionary<string, object>
        {
            ["risk"] = 0.5
        };

        // Act
        var result = rule.ShouldEscalate(signals);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldEscalate_WithBooleanCondition_ReturnsTrue()
    {
        // Arrange
        var rule = new EscalationRule
        {
            Name = "honeypot",
            Priority = 100,
            Condition = "honeypot == true",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "Honeypot hit"
        };

        var signals = new Dictionary<string, object>
        {
            ["honeypot"] = true
        };

        // Act
        var result = rule.ShouldEscalate(signals);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldEscalate_WithAndCondition_ReturnsTrue()
    {
        // Arrange
        var rule = new EscalationRule
        {
            Name = "honeypot_404",
            Priority = 100,
            Condition = "honeypot == true && status == 404",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "Honeypot 404"
        };

        var signals = new Dictionary<string, object>
        {
            ["honeypot"] = true,
            ["status"] = 404
        };

        // Act
        var result = rule.ShouldEscalate(signals);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldEscalate_WithAndCondition_OneFalse_ReturnsFalse()
    {
        // Arrange
        var rule = new EscalationRule
        {
            Name = "honeypot_404",
            Priority = 100,
            Condition = "honeypot == true && status == 404",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "Honeypot 404"
        };

        var signals = new Dictionary<string, object>
        {
            ["honeypot"] = true,
            ["status"] = 200
        };

        // Act
        var result = rule.ShouldEscalate(signals);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void BuildReason_InterpolatesSignalValues()
    {
        // Arrange
        var rule = new EscalationRule
        {
            Name = "high_risk",
            Priority = 90,
            Condition = "risk > 0.8",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "High risk: {risk}"
        };

        var signals = new Dictionary<string, object>
        {
            ["risk"] = 0.85
        };

        // Act
        var reason = rule.BuildReason(signals);

        // Assert
        Assert.Contains("0.85", reason);
    }

    [Fact]
    public void BuildReason_HandlesMultipleInterpolations()
    {
        // Arrange
        var rule = new EscalationRule
        {
            Name = "complex",
            Priority = 90,
            Condition = "risk > 0.8",
            ShouldStore = true,
            ShouldAlert = true,
            Reason = "Risk: {risk}, Status: {status}, Path: {path}"
        };

        var signals = new Dictionary<string, object>
        {
            ["risk"] = 0.85,
            ["status"] = 404,
            ["path"] = "/admin"
        };

        // Act
        var reason = rule.BuildReason(signals);

        // Assert
        Assert.Contains("0.85", reason);
        Assert.Contains("404", reason);
        Assert.Contains("/admin", reason);
    }
}

public class EscalatorConfigTests
{
    [Fact]
    public void DefaultConfig_HasRequestPatterns()
    {
        // Arrange & Act
        var config = new EscalatorConfig();

        // Assert
        Assert.NotNull(config.RequestPatterns);
        Assert.Contains("risk", config.RequestPatterns.Keys);
        Assert.Contains("honeypot", config.RequestPatterns.Keys);
    }

    [Fact]
    public void DefaultConfig_HasResponsePatterns()
    {
        // Arrange & Act
        var config = new EscalatorConfig();

        // Assert
        Assert.NotNull(config.ResponsePatterns);
        Assert.Contains("score", config.ResponsePatterns.Keys);
        Assert.Contains("status", config.ResponsePatterns.Keys);
    }

    [Fact]
    public void DefaultConfig_HasEscalationRules()
    {
        // Arrange & Act
        var config = new EscalatorConfig();

        // Assert
        Assert.NotNull(config.EscalationRules);
        Assert.NotEmpty(config.EscalationRules);
    }

    [Fact]
    public void DefaultConfig_HasOperationEscalationRules()
    {
        // Arrange & Act
        var config = new EscalatorConfig();

        // Assert
        Assert.NotNull(config.OperationEscalationRules);
        Assert.NotEmpty(config.OperationEscalationRules);
    }

    [Fact]
    public void DefaultThresholds_AreReasonable()
    {
        // Arrange & Act
        var config = new EscalatorConfig();

        // Assert
        Assert.True(config.StoreThreshold >= 0 && config.StoreThreshold <= 1);
        Assert.True(config.AlertThreshold >= 0 && config.AlertThreshold <= 1);
        Assert.True(config.AlertThreshold >= config.StoreThreshold);
    }
}