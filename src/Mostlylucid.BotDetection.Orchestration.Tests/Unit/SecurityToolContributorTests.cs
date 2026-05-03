using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Unit tests for SecurityToolContributor.
///     Tests detection of penetration testing tools, vulnerability scanners, and exploit frameworks.
/// </summary>
public class SecurityToolContributorTests
{
    private readonly Mock<IBotListFetcher> _fetcherMock;
    private readonly Mock<ILogger<SecurityToolContributor>> _loggerMock;
    private readonly Mock<IDetectorConfigProvider> _configProviderMock;
    private readonly BotDetectionOptions _options;

    public SecurityToolContributorTests()
    {
        _loggerMock = new Mock<ILogger<SecurityToolContributor>>();
        _fetcherMock = new Mock<IBotListFetcher>();
        _configProviderMock = new Mock<IDetectorConfigProvider>();

        // Setup default config
        _configProviderMock.Setup(c => c.GetDefaults(It.IsAny<string>()))
            .Returns(new DetectorDefaults());
        _configProviderMock.Setup(c => c.GetManifest(It.IsAny<string>()))
            .Returns((DetectorManifest?)null);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string _, string _, int def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Returns((string _, string _, double def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, string _, bool def) => def);

        _options = new BotDetectionOptions
        {
            SecurityTools = new SecurityToolOptions { Enabled = true }
        };
    }

    private SecurityToolContributor CreateContributor()
    {
        return new SecurityToolContributor(
            _loggerMock.Object,
            Options.Create(_options),
            _fetcherMock.Object,
            _configProviderMock.Object);
    }

    private BlackboardState CreateState(string userAgent, string? clientIp = "192.168.1.100")
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.UserAgent = userAgent;

        var signalDict = new ConcurrentDictionary<string, object>(new Dictionary<string, object>
        {
            [SignalKeys.ClientIp] = clientIp ?? ""
        });
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
    // Basic Detection Tests
    // ==========================================

    [Theory]
    [InlineData("sqlmap/1.5#stable (http://sqlmap.org)", "Sqlmap")]
    [InlineData("Nikto/2.1.6", "Nikto")]
    [InlineData("Nmap Scripting Engine; https://nmap.org/book/nse.html", "Nmap")]
    [InlineData("Mozilla/5.0 (compatible; Nessus SOAP)", "Nessus")]
    [InlineData("gobuster/3.1.0", "Gobuster")]
    [InlineData("feroxbuster/2.7.0", "Feroxbuster")]
    [InlineData("Fuzz Faster U Fool v1.3.1", "Ffuf")]
    [InlineData("WPScan v3.8.22 (https://wpscan.com/)", "Wpscan")]
    [InlineData("Acunetix-Product", "Acunetix")]
    [InlineData("masscan/1.3", "Masscan")]
    [InlineData("nuclei - Open-source project", "Nuclei")]
    [InlineData("Metasploit/5.0.0", "Metasploit")]
    [InlineData("hydra-http-form-post", "Hydra")]
    public async Task ContributeAsync_DetectsSecurityTools(string userAgent, string expectedToolName)
    {
        // Arrange
        SetupFetcherWithFallbackPatterns();
        var contributor = CreateContributor();
        var state = CreateState(userAgent);

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Single(contributions);
        var contribution = contributions[0];
        Assert.True(contribution.TriggerEarlyExit);
        Assert.Equal("VerifiedBadBot", contribution.EarlyExitVerdict);
        // BotType contains the full detection message (this is current behavior)
        Assert.Contains("Security", contribution.BotType ?? "");
        Assert.True(contribution.ConfidenceDelta >= 0.9);
        // BotName is set via the Reason parameter in current implementation
        Assert.Contains(expectedToolName.ToLowerInvariant(),
            contribution.Reason?.ToLowerInvariant() ?? "");

        // Verify signals are written to shared state
        Assert.True(state.Signals.ContainsKey(SignalKeys.SecurityToolDetected));
        Assert.True((bool)state.Signals[SignalKeys.SecurityToolDetected]);
    }

    [Fact]
    public async Task ContributeAsync_NormalUserAgent_ReturnsNeutral()
    {
        // Arrange
        SetupFetcherWithFallbackPatterns();
        var contributor = CreateContributor();
        var state = CreateState("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert - Returns neutral contribution when no security tool detected
        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
        Assert.Contains("No security tools detected", contributions[0].Reason);
    }

    [Fact]
    public async Task ContributeAsync_EmptyUserAgent_ReturnsEmpty()
    {
        // Arrange
        SetupFetcherWithFallbackPatterns();
        var contributor = CreateContributor();
        var state = CreateState("");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Empty(contributions);
    }

    [Fact]
    public async Task ContributeAsync_WhitespaceUserAgent_ReturnsEmpty()
    {
        // Arrange
        SetupFetcherWithFallbackPatterns();
        var contributor = CreateContributor();
        var state = CreateState("   ");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Empty(contributions);
    }

    // ==========================================
    // Configuration Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_WhenDisabled_ReturnsEmpty()
    {
        // Arrange
        _options.SecurityTools.Enabled = false;
        SetupFetcherWithFallbackPatterns();
        var contributor = CreateContributor();
        var state = CreateState("sqlmap/1.5#stable");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Empty(contributions);
    }

    [Fact]
    public async Task ContributeAsync_NoPatternsAvailable_ReturnsEmpty()
    {
        // Arrange
        _fetcherMock.Setup(f => f.GetSecurityToolPatternsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SecurityToolPattern>());
        var contributor = CreateContributor();
        var state = CreateState("sqlmap/1.5#stable");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Empty(contributions);
    }

    // ==========================================
    // Pattern Matching Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_CaseInsensitiveMatching()
    {
        // Arrange
        SetupFetcherWithFallbackPatterns();
        var contributor = CreateContributor();
        var state = CreateState("SQLMAP/1.5");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Single(contributions);
        Assert.Contains("sqlmap", contributions[0].Reason?.ToLowerInvariant() ?? "");
    }

    [Fact]
    public async Task ContributeAsync_RegexPattern_Matches()
    {
        // Arrange
        var patterns = new List<SecurityToolPattern>
        {
            new() { Pattern = @"sqlmap[/\s]?\d", Name = "SQLMap", Category = "SqlInjection", IsRegex = true }
        };
        _fetcherMock.Setup(f => f.GetSecurityToolPatternsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(patterns);
        var contributor = CreateContributor();
        var state = CreateState("sqlmap/1.5#stable");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Single(contributions);
    }

    [Fact]
    public async Task ContributeAsync_RegexPattern_NoMatch()
    {
        // Arrange
        var patterns = new List<SecurityToolPattern>
        {
            new() { Pattern = @"sqlmap[/\s]?\d", Name = "SQLMap", Category = "SqlInjection", IsRegex = true }
        };
        _fetcherMock.Setup(f => f.GetSecurityToolPatternsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(patterns);
        var contributor = CreateContributor();
        var state = CreateState("I like sqlmap but this shouldn't match");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert - Returns neutral contribution when pattern doesn't match
        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
        Assert.Contains("No security tools detected", contributions[0].Reason);
    }

    // ==========================================
    // Signal Emission Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_EmitsCorrectSignals()
    {
        // Arrange
        SetupFetcherWithFallbackPatterns();
        var contributor = CreateContributor();
        var state = CreateState("Nikto/2.1.6");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Single(contributions);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SecurityToolDetected));
        Assert.True((bool)state.Signals[SignalKeys.SecurityToolDetected]);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SecurityToolName));
        Assert.Equal("Nikto", state.Signals[SignalKeys.SecurityToolName]);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SecurityToolCategory));

        Assert.True(state.Signals.ContainsKey(SignalKeys.UserAgent));
        Assert.Equal("Nikto/2.1.6", state.Signals[SignalKeys.UserAgent]);

        Assert.True(state.Signals.ContainsKey(SignalKeys.UserAgentIsBot));
        Assert.True((bool)state.Signals[SignalKeys.UserAgentIsBot]);

        Assert.True(state.Signals.ContainsKey(SignalKeys.UserAgentBotType));
        Assert.Equal(BotType.MaliciousBot.ToString(), state.Signals[SignalKeys.UserAgentBotType]);
    }

    // ==========================================
    // Caching Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_CachesPatterns()
    {
        // Arrange
        SetupFetcherWithFallbackPatterns();
        var contributor = CreateContributor();
        var state = CreateState("sqlmap/1.5");

        // Act - Call twice
        await contributor.ContributeAsync(state);
        await contributor.ContributeAsync(state);

        // Assert - Fetcher should only be called once due to caching
        _fetcherMock.Verify(f => f.GetSecurityToolPatternsAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ==========================================
    // Error Handling Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_FetcherThrows_ReturnsEmpty()
    {
        // Arrange
        _fetcherMock.Setup(f => f.GetSecurityToolPatternsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));
        var contributor = CreateContributor();
        var state = CreateState("sqlmap/1.5");

        // Act
        var contributions = await contributor.ContributeAsync(state);

        // Assert
        Assert.Empty(contributions);
    }

    // ==========================================
    // Properties Tests
    // ==========================================

    [Fact]
    public void Name_ReturnsSecurityTool()
    {
        var contributor = CreateContributor();
        Assert.Equal("SecurityTool", contributor.Name);
    }

    [Fact]
    public void Priority_IsLow_RunsEarly()
    {
        var contributor = CreateContributor();
        Assert.Equal(8, contributor.Priority);
    }

    [Fact]
    public void TriggerConditions_IsEmpty_RunsInFirstWave()
    {
        var contributor = CreateContributor();
        Assert.Empty(contributor.TriggerConditions);
    }

    // ==========================================
    // Helper Methods
    // ==========================================

    private void SetupFetcherWithFallbackPatterns()
    {
        var fallbackPatterns = new List<SecurityToolPattern>
        {
            new() { Pattern = "sqlmap", Name = "Sqlmap", Category = "SqlInjection", IsRegex = false },
            new() { Pattern = "nikto", Name = "Nikto", Category = "VulnerabilityScanner", IsRegex = false },
            new() { Pattern = "nmap", Name = "Nmap", Category = "PortScanner", IsRegex = false },
            new() { Pattern = "nessus", Name = "Nessus", Category = "VulnerabilityScanner", IsRegex = false },
            new() { Pattern = "gobuster", Name = "Gobuster", Category = "DirectoryBruteForce", IsRegex = false },
            new() { Pattern = "feroxbuster", Name = "Feroxbuster", Category = "DirectoryBruteForce", IsRegex = false },
            new() { Pattern = "ffuf", Name = "Ffuf", Category = "DirectoryBruteForce", IsRegex = false },
            new() { Pattern = "Fuzz Faster U Fool", Name = "Ffuf", Category = "DirectoryBruteForce", IsRegex = false },
            new() { Pattern = "wpscan", Name = "Wpscan", Category = "CmsScanner", IsRegex = false },
            new() { Pattern = "acunetix", Name = "Acunetix", Category = "VulnerabilityScanner", IsRegex = false },
            new() { Pattern = "masscan", Name = "Masscan", Category = "PortScanner", IsRegex = false },
            new() { Pattern = "nuclei", Name = "Nuclei", Category = "VulnerabilityScanner", IsRegex = false },
            new() { Pattern = "metasploit", Name = "Metasploit", Category = "ExploitFramework", IsRegex = false },
            new() { Pattern = "hydra", Name = "Hydra", Category = "CredentialAttack", IsRegex = false }
        };

        _fetcherMock.Setup(f => f.GetSecurityToolPatternsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(fallbackPatterns);
    }
}