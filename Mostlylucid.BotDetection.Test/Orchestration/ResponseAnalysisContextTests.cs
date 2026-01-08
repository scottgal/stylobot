using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public class ResponseAnalysisContextTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        // Arrange & Act
        var context = new ResponseAnalysisContext { ClientId = "test-client" };

        // Assert
        Assert.False(context.EnableAnalysis);
        Assert.Equal(ResponseAnalysisMode.Async, context.Mode);
        Assert.False(context.EnableStreaming);
        Assert.Equal(ResponseAnalysisThoroughness.Standard, context.Thoroughness);
        Assert.NotNull(context.TriggerSignals);
        Assert.Empty(context.TriggerSignals);
    }

    [Fact]
    public void EnableAnalysis_CanBeSet()
    {
        // Arrange
        var context = new ResponseAnalysisContext { ClientId = "test-client" };

        // Act
        context.EnableAnalysis = true;

        // Assert
        Assert.True(context.EnableAnalysis);
    }

    [Fact]
    public void Mode_CanBeSetToBlocking()
    {
        // Arrange
        var context = new ResponseAnalysisContext { ClientId = "test-client" };

        // Act
        context.Mode = ResponseAnalysisMode.Inline;

        // Assert
        Assert.Equal(ResponseAnalysisMode.Inline, context.Mode);
    }

    [Fact]
    public void Thoroughness_CanBeSetToDeep()
    {
        // Arrange
        var context = new ResponseAnalysisContext { ClientId = "test-client" };

        // Act
        context.Thoroughness = ResponseAnalysisThoroughness.Deep;

        // Assert
        Assert.Equal(ResponseAnalysisThoroughness.Deep, context.Thoroughness);
    }

    [Fact]
    public void TriggerSignals_CanBePopulated()
    {
        // Arrange
        var context = new ResponseAnalysisContext { ClientId = "test-client" };

        // Act
        context.TriggerSignals["honeypot"] = true;
        context.TriggerSignals["datacenter"] = "AWS";
        context.TriggerSignals["risk"] = 0.85;

        // Assert
        Assert.Equal(3, context.TriggerSignals.Count);
        Assert.True((bool)context.TriggerSignals["honeypot"]);
        Assert.Equal("AWS", context.TriggerSignals["datacenter"]);
        Assert.Equal(0.85, context.TriggerSignals["risk"]);
    }

    [Fact]
    public void EnableStreaming_CanBeEnabled()
    {
        // Arrange
        var context = new ResponseAnalysisContext { ClientId = "test-client" };

        // Act
        context.EnableStreaming = true;

        // Assert
        Assert.True(context.EnableStreaming);
    }

    [Theory]
    [InlineData(ResponseAnalysisThoroughness.Minimal)]
    [InlineData(ResponseAnalysisThoroughness.Standard)]
    [InlineData(ResponseAnalysisThoroughness.Thorough)]
    [InlineData(ResponseAnalysisThoroughness.Deep)]
    public void Thoroughness_AllLevelsWork(ResponseAnalysisThoroughness level)
    {
        // Arrange
        var context = new ResponseAnalysisContext { ClientId = "test-client" };

        // Act
        context.Thoroughness = level;

        // Assert
        Assert.Equal(level, context.Thoroughness);
    }

    [Fact]
    public void ConfiguredForHoneypot_HasCorrectSettings()
    {
        // Arrange & Act
        var context = new ResponseAnalysisContext
        {
            ClientId = "test-client",
            EnableAnalysis = true,
            Mode = ResponseAnalysisMode.Inline,
            Thoroughness = ResponseAnalysisThoroughness.Deep,
            TriggerSignals = new Dictionary<string, object>
            {
                ["honeypot"] = true,
                ["path"] = "/admin"
            }
        };

        // Assert
        Assert.True(context.EnableAnalysis);
        Assert.Equal(ResponseAnalysisMode.Inline, context.Mode);
        Assert.Equal(ResponseAnalysisThoroughness.Deep, context.Thoroughness);
        Assert.True((bool)context.TriggerSignals["honeypot"]);
    }

    [Fact]
    public void ConfiguredForDatacenter_HasCorrectSettings()
    {
        // Arrange & Act
        var context = new ResponseAnalysisContext
        {
            ClientId = "test-client",
            EnableAnalysis = true,
            Mode = ResponseAnalysisMode.Async,
            Thoroughness = ResponseAnalysisThoroughness.Thorough,
            TriggerSignals = new Dictionary<string, object>
            {
                ["datacenter"] = "AWS",
                ["risk"] = 0.75
            }
        };

        // Assert
        Assert.True(context.EnableAnalysis);
        Assert.Equal(ResponseAnalysisMode.Async, context.Mode);
        Assert.Equal(ResponseAnalysisThoroughness.Thorough, context.Thoroughness);
        Assert.Equal("AWS", context.TriggerSignals["datacenter"]);
    }
}

/*
/// <summary>
/// OBSOLETE TESTS - These types were removed in blackboard architecture migration
/// See BLACKBOARD_ARCHITECTURE.md for details
/// </summary>
public class ResponseSignalTests
{
    [Fact(Skip = "Obsolete: ResponseSignal removed in blackboard architecture")]
    public void ResponseSignal_RequiredPropertiesCanBeSet()
    {
        // Arrange & Act
        var signal = new ResponseSignal
        {
            RequestId = "req-123",
            ClientId = "client-abc",
            Timestamp = DateTimeOffset.UtcNow,
            StatusCode = 404,
            BodySummary = new ResponseBodySummary
            {
                MatchedPatterns = new List<string> { "error_template", "404_pattern" },
                TemplateId = "error_404",
                BodyHash = "hash123"
            },
            Headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "text/html",
                ["Server"] = "nginx"
            },
            ResponseTimeMs = 125
        };

        // Assert
        Assert.Equal("req-123", signal.RequestId);
        Assert.Equal("client-abc", signal.ClientId);
        Assert.Equal(404, signal.StatusCode);
        Assert.NotNull(signal.BodySummary);
        Assert.Equal(2, signal.BodySummary.MatchedPatterns.Count);
        Assert.Equal("error_404", signal.BodySummary.TemplateId);
        Assert.Equal(125, signal.ResponseTimeMs);
    }

    [Fact]
    public void ResponseBodySummary_OnlyStoresPatternNames()
    {
        // Arrange & Act
        var summary = new ResponseBodySummary
        {
            MatchedPatterns = new List<string> { "login_form", "csrf_token" },
            TemplateId = "login_page",
            BodyHash = "abc123def456"
        };

        // Assert
        Assert.Equal(2, summary.MatchedPatterns.Count);
        Assert.Contains("login_form", summary.MatchedPatterns);
        Assert.Contains("csrf_token", summary.MatchedPatterns);
        Assert.Equal("login_page", summary.TemplateId);
        Assert.NotNull(summary.BodyHash);
    }

    [Fact]
    public void ResponseSignal_CanHaveEmptyPatterns()
    {
        // Arrange & Act
        var signal = new ResponseSignal
        {
            RequestId = "req-123",
            ClientId = "client-abc",
            Timestamp = DateTimeOffset.UtcNow,
            StatusCode = 200,
            BodySummary = new ResponseBodySummary
            {
                MatchedPatterns = new List<string>(),
                TemplateId = null,
                BodyHash = "hash"
            },
            Headers = new Dictionary<string, string>(),
            ResponseTimeMs = 50
        };

        // Assert
        Assert.Empty(signal.BodySummary.MatchedPatterns);
        Assert.Null(signal.BodySummary.TemplateId);
    }
}

public class OperationCompleteSignalTests
{
    [Fact]
    public void OperationCompleteSignal_RequiredPropertiesCanBeSet()
    {
        // Arrange & Act
        var signal = new OperationCompleteSignal
        {
            Signature = "sig-abc",
            RequestId = "req-123",
            Timestamp = DateTimeOffset.UtcNow,
            Priority = 90,
            RequestRisk = 0.75,
            Path = "/api/users",
            Method = "GET",
            ResponseScore = 0.80,
            StatusCode = 404,
            ResponseBytes = 1024,
            CombinedScore = 0.85,
            Honeypot = true,
            Datacenter = "AWS",
            TriggerSignals = new Dictionary<string, object>
            {
                ["honeypot"] = true,
                ["datacenter"] = "AWS"
            }
        };

        // Assert
        Assert.Equal("sig-abc", signal.Signature);
        Assert.Equal(90, signal.Priority);
        Assert.Equal(0.75, signal.RequestRisk);
        Assert.Equal(0.80, signal.ResponseScore);
        Assert.Equal(0.85, signal.CombinedScore);
        Assert.True(signal.Honeypot);
        Assert.Equal("AWS", signal.Datacenter);
        Assert.Equal(2, signal.TriggerSignals.Count);
    }

    [Fact]
    public void RequestCompleteSignal_RequiredPropertiesCanBeSet()
    {
        // Arrange & Act
        var signal = new RequestCompleteSignal
        {
            Signature = "sig-abc",
            RequestId = "req-123",
            Timestamp = DateTimeOffset.UtcNow,
            Risk = 0.85,
            Honeypot = true,
            Datacenter = "AWS",
            Path = "/admin",
            Method = "POST",
            TriggerSignals = new Dictionary<string, object>
            {
                ["honeypot"] = true
            }
        };

        // Assert
        Assert.Equal("sig-abc", signal.Signature);
        Assert.Equal(0.85, signal.Risk);
        Assert.True(signal.Honeypot);
        Assert.Equal("AWS", signal.Datacenter);
        Assert.Equal("/admin", signal.Path);
        Assert.Equal("POST", signal.Method);
    }
}

public class EscalationDecisionTests
{
    [Fact]
    public void EscalationDecision_RequiredPropertiesCanBeSet()
    {
        // Arrange & Act
        var decision = new EscalationDecision
        {
            ShouldEscalate = true,
            Priority = 90,
            Reason = "High risk detected",
            ShouldStore = true,
            ShouldAlert = true
        };

        // Assert
        Assert.True(decision.ShouldEscalate);
        Assert.Equal(90, decision.Priority);
        Assert.Equal("High risk detected", decision.Reason);
        Assert.True(decision.ShouldStore);
        Assert.True(decision.ShouldAlert);
    }

    [Fact]
    public void EscalationDecision_OptionalPropertiesDefault()
    {
        // Arrange & Act
        var decision = new EscalationDecision
        {
            ShouldEscalate = false,
            Priority = 0,
            Reason = "No escalation"
        };

        // Assert
        Assert.False(decision.ShouldEscalate);
        Assert.False(decision.ShouldStore);
        Assert.False(decision.ShouldAlert);
    }
}
*/