using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Unit tests for ProjectHoneypotContributor.
///     Tests IP reputation checking via Project Honeypot's HTTP:BL service.
/// </summary>
public class ProjectHoneypotContributorTests
{
    private readonly Mock<ILogger<ProjectHoneypotContributor>> _loggerMock;
    private readonly BotDetectionOptions _options;

    public ProjectHoneypotContributorTests()
    {
        _loggerMock = new Mock<ILogger<ProjectHoneypotContributor>>();
        _options = new BotDetectionOptions
        {
            ProjectHoneypot = new ProjectHoneypotOptions
            {
                Enabled = true,
                AccessKey = "testkey12345",
                HighThreatThreshold = 25
            }
        };
    }

    private ProjectHoneypotContributor CreateContributor()
    {
        return new ProjectHoneypotContributor(
            _loggerMock.Object,
            Options.Create(_options));
    }

    private BlackboardState CreateState(string? clientIp, bool isLocal = false)
    {
        var httpContext = new DefaultHttpContext();
        var signalDict = new ConcurrentDictionary<string, object>();

        if (clientIp != null) signalDict[SignalKeys.ClientIp] = clientIp;
        signalDict[SignalKeys.IpIsLocal] = isLocal;

        return new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };
    }

    // ==========================================
    // Configuration Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_WhenDisabled_ReturnsSkipMessage()
    {
        // Arrange
        _options.ProjectHoneypot.Enabled = false;
        var contributor = CreateContributor();
        var state = CreateState("8.8.8.8");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert - Returns neutral contribution with skip message
        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
        Assert.Contains("disabled", contributions[0].Reason);
    }

    [Fact]
    public async Task ContributeAsync_NoAccessKey_ReturnsSkipMessage()
    {
        // Arrange
        _options.ProjectHoneypot.AccessKey = null;
        var contributor = CreateContributor();
        var state = CreateState("8.8.8.8");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert - Returns neutral contribution with skip message
        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
        Assert.Contains("not configured", contributions[0].Reason);
    }

    [Fact]
    public async Task ContributeAsync_EmptyAccessKey_ReturnsSkipMessage()
    {
        // Arrange
        _options.ProjectHoneypot.AccessKey = "";
        var contributor = CreateContributor();
        var state = CreateState("8.8.8.8");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert - Returns neutral contribution with skip message
        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
        Assert.Contains("not configured", contributions[0].Reason);
    }

    // ==========================================
    // IP Validation Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_NoIpSignal_ReturnsEmpty()
    {
        // Arrange
        var contributor = CreateContributor();
        var state = CreateState(null);

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Empty(contributions);
    }

    [Fact]
    public async Task ContributeAsync_EmptyIp_ReturnsEmpty()
    {
        // Arrange
        var contributor = CreateContributor();
        var state = CreateState("");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Empty(contributions);
    }

    [Fact]
    public async Task ContributeAsync_LocalIp_ReturnsSkipMessage()
    {
        // Arrange
        var contributor = CreateContributor();
        var state = CreateState("192.168.1.1", true);

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert - Returns neutral contribution with skip message
        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
        Assert.Contains("localhost", contributions[0].Reason.ToLowerInvariant());
    }

    [Fact]
    public async Task ContributeAsync_InvalidIp_ReturnsEmpty()
    {
        // Arrange
        var contributor = CreateContributor();
        var state = CreateState("not-an-ip");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Empty(contributions);
    }

    [Fact]
    public async Task ContributeAsync_IPv6Address_ReturnsEmpty()
    {
        // Arrange - IPv6 is not supported by HTTP:BL
        var contributor = CreateContributor();
        var state = CreateState("2001:4860:4860::8888");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Empty(contributions);
    }

    // ==========================================
    // Properties Tests
    // ==========================================

    [Fact]
    public void Name_ReturnsProjectHoneypot()
    {
        var contributor = CreateContributor();
        Assert.Equal("ProjectHoneypot", contributor.Name);
    }

    [Fact]
    public void Priority_Is15_RunsAfterIpAnalysis()
    {
        var contributor = CreateContributor();
        Assert.Equal(15, contributor.Priority);
    }

    [Fact]
    public void ExecutionTimeout_Is1Second()
    {
        var contributor = CreateContributor();
        Assert.Equal(TimeSpan.FromMilliseconds(1000), contributor.ExecutionTimeout);
    }

    [Fact]
    public void IsOptional_IsTrue()
    {
        var contributor = CreateContributor();
        Assert.True(contributor.IsOptional);
    }

    [Fact]
    public void TriggerConditions_RequiresClientIp()
    {
        var contributor = CreateContributor();
        Assert.Single(contributor.TriggerConditions);
    }

    // ==========================================
    // HoneypotVisitorType Tests
    // ==========================================

    [Fact]
    public void HoneypotVisitorType_None_HasValue0()
    {
        Assert.Equal(0, (int)HoneypotVisitorType.None);
    }

    [Fact]
    public void HoneypotVisitorType_Suspicious_HasValue1()
    {
        Assert.Equal(1, (int)HoneypotVisitorType.Suspicious);
    }

    [Fact]
    public void HoneypotVisitorType_Harvester_HasValue2()
    {
        Assert.Equal(2, (int)HoneypotVisitorType.Harvester);
    }

    [Fact]
    public void HoneypotVisitorType_CommentSpammer_HasValue4()
    {
        Assert.Equal(4, (int)HoneypotVisitorType.CommentSpammer);
    }

    [Fact]
    public void HoneypotVisitorType_SearchEngine_HasSentinelValue()
    {
        // SearchEngine is a special value (byte 0), stored as 256 (outside byte range)
        Assert.Equal(256, (int)HoneypotVisitorType.SearchEngine);
    }

    [Fact]
    public void HoneypotVisitorType_FlagsCanBeCombined()
    {
        var combined = HoneypotVisitorType.Suspicious | HoneypotVisitorType.Harvester;
        Assert.Equal(3, (int)combined);
        Assert.True(combined.HasFlag(HoneypotVisitorType.Suspicious));
        Assert.True(combined.HasFlag(HoneypotVisitorType.Harvester));
        Assert.False(combined.HasFlag(HoneypotVisitorType.CommentSpammer));
    }

    [Fact]
    public void HoneypotVisitorType_AllThreeFlagsCanBeCombined()
    {
        var combined = HoneypotVisitorType.Suspicious |
                       HoneypotVisitorType.Harvester |
                       HoneypotVisitorType.CommentSpammer;
        Assert.Equal(7, (int)combined);
    }

    // ==========================================
    // HoneypotResult Tests
    // ==========================================

    [Fact]
    public void HoneypotResult_DefaultIsNotListed()
    {
        var result = new HoneypotResult();
        Assert.False(result.IsListed);
        Assert.Equal(0, result.DaysSinceLastActivity);
        Assert.Equal(0, result.ThreatScore);
        Assert.Equal(HoneypotVisitorType.None, result.VisitorType);
    }

    [Fact]
    public void HoneypotResult_CanSetAllProperties()
    {
        var result = new HoneypotResult
        {
            IsListed = true,
            DaysSinceLastActivity = 7,
            ThreatScore = 50,
            VisitorType = HoneypotVisitorType.Harvester | HoneypotVisitorType.CommentSpammer
        };

        Assert.True(result.IsListed);
        Assert.Equal(7, result.DaysSinceLastActivity);
        Assert.Equal(50, result.ThreatScore);
        Assert.True(result.VisitorType.HasFlag(HoneypotVisitorType.Harvester));
        Assert.True(result.VisitorType.HasFlag(HoneypotVisitorType.CommentSpammer));
    }
}

/// <summary>
///     Tests for the HTTP:BL response parsing logic.
///     These test the parsing without actual DNS lookups.
/// </summary>
public class HoneypotResponseParsingTests
{
    [Theory]
    [InlineData(0, HoneypotVisitorType.SearchEngine)]
    [InlineData(1, HoneypotVisitorType.Suspicious)]
    [InlineData(2, HoneypotVisitorType.Harvester)]
    [InlineData(4, HoneypotVisitorType.CommentSpammer)]
    [InlineData(3, HoneypotVisitorType.Suspicious | HoneypotVisitorType.Harvester)]
    [InlineData(5, HoneypotVisitorType.Suspicious | HoneypotVisitorType.CommentSpammer)]
    [InlineData(6, HoneypotVisitorType.Harvester | HoneypotVisitorType.CommentSpammer)]
    [InlineData(7, HoneypotVisitorType.Suspicious | HoneypotVisitorType.Harvester | HoneypotVisitorType.CommentSpammer)]
    public void ParseVisitorType_ReturnsCorrectFlags(byte typeByte, HoneypotVisitorType expected)
    {
        // Use reflection to test the private method, or extract it
        var result = ParseVisitorTypePublic(typeByte);
        Assert.Equal(expected, result);
    }

    /// <summary>
    ///     Public implementation of the parsing logic for testing.
    ///     Mirrors the private ParseVisitorType method in ProjectHoneypotContributor.
    /// </summary>
    private static HoneypotVisitorType ParseVisitorTypePublic(byte typeByte)
    {
        if (typeByte == 0)
            return HoneypotVisitorType.SearchEngine;

        var type = HoneypotVisitorType.None;

        if ((typeByte & 1) != 0)
            type |= HoneypotVisitorType.Suspicious;
        if ((typeByte & 2) != 0)
            type |= HoneypotVisitorType.Harvester;
        if ((typeByte & 4) != 0)
            type |= HoneypotVisitorType.CommentSpammer;

        return type;
    }

    [Theory]
    [InlineData(0, 0.30)] // No threat
    [InlineData(5, 0.40)] // Low threat
    [InlineData(10, 0.55)] // Low-medium threat
    [InlineData(25, 0.70)] // Medium threat
    [InlineData(50, 0.85)] // High threat
    [InlineData(100, 0.95)] // Very high threat
    [InlineData(255, 0.95)] // Maximum threat
    public void CalculateConfidence_ThreatScore_ReturnsCorrectBase(int threatScore, double expectedBase)
    {
        // This tests the base confidence before age/type adjustments
        // With days=0 (today) and no type modifiers, we should get the base
        var result = CalculateConfidencePublic(threatScore, 0, HoneypotVisitorType.Suspicious);

        // Suspicious adds 5% multiplier, so expected is baseConfidence * 1.05
        var expected = Math.Min(expectedBase * 1.05, 0.99);
        Assert.Equal(expected, result, 2);
    }

    [Theory]
    [InlineData(0, 1.0)] // Today
    [InlineData(7, 0.95)] // Last week
    [InlineData(30, 0.85)] // Last month
    [InlineData(90, 0.70)] // Last quarter
    [InlineData(180, 0.50)] // Last 6 months
    [InlineData(365, 0.30)] // Older
    public void CalculateConfidence_Age_ReducesConfidence(int daysAgo, double ageFactor)
    {
        // With threat score 100 (base 0.95) and Suspicious type (1.05x)
        var result = CalculateConfidencePublic(100, daysAgo, HoneypotVisitorType.Suspicious);
        var expected = Math.Min(0.95 * ageFactor * 1.05, 0.99);
        Assert.Equal(expected, result, 2);
    }

    [Fact]
    public void CalculateConfidence_CommentSpammer_IncreasesConfidence()
    {
        // CommentSpammer adds 10%
        var withoutSpammer = CalculateConfidencePublic(50, 0, HoneypotVisitorType.Harvester);
        var withSpammer =
            CalculateConfidencePublic(50, 0, HoneypotVisitorType.Harvester | HoneypotVisitorType.CommentSpammer);

        Assert.True(withSpammer > withoutSpammer);
    }

    /// <summary>
    ///     Public implementation of confidence calculation for testing.
    ///     Mirrors the private CalculateConfidence method in ProjectHoneypotContributor.
    /// </summary>
    private static double CalculateConfidencePublic(int threatScore, int daysAgo, HoneypotVisitorType visitorType)
    {
        var baseConfidence = threatScore switch
        {
            >= 100 => 0.95,
            >= 50 => 0.85,
            >= 25 => 0.70,
            >= 10 => 0.55,
            >= 5 => 0.40,
            _ => 0.30
        };

        var ageFactor = daysAgo switch
        {
            0 => 1.0,
            <= 7 => 0.95,
            <= 30 => 0.85,
            <= 90 => 0.70,
            <= 180 => 0.50,
            _ => 0.30
        };

        var typeFactor = 1.0;
        if (visitorType.HasFlag(HoneypotVisitorType.CommentSpammer))
            typeFactor *= 1.1;
        if (visitorType.HasFlag(HoneypotVisitorType.Harvester))
            typeFactor *= 1.15;
        if (visitorType.HasFlag(HoneypotVisitorType.Suspicious))
            typeFactor *= 1.05;

        return Math.Min(baseConfidence * ageFactor * typeFactor, 0.99);
    }
}