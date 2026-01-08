using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Test.Helpers;

namespace Mostlylucid.BotDetection.Test.Detectors;

/// <summary>
///     Comprehensive tests for IpDetector
/// </summary>
public class IpDetectorTests
{
    private readonly ILogger<IpDetector> _logger;

    public IpDetectorTests()
    {
        _logger = new Mock<ILogger<IpDetector>>().Object;
    }

    private IpDetector CreateDetector(BotDetectionOptions? options = null)
    {
        return new IpDetector(
            _logger,
            Options.Create(options ?? new BotDetectionOptions()));
    }

    #region Missing IP Tests

    [Fact]
    public async Task DetectAsync_NoIpAddress_ReturnsZeroConfidence()
    {
        // Arrange
        var detector = CreateDetector();
        var context = new DefaultHttpContext();
        // No IP set

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Equal(0.0, result.Confidence);
        Assert.Empty(result.Reasons);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task DetectAsync_WithCancellation_CompletesNormally()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");
        using var cts = new CancellationTokenSource();

        // Act
        var result = await detector.DetectAsync(context, cts.Token);

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    #region Name Property Test

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        // Arrange
        var detector = CreateDetector();

        // Assert
        Assert.Equal("IP Detector", detector.Name);
    }

    #endregion

    #region Datacenter IP Tests

    [Theory]
    [InlineData("3.1.2.3")] // AWS
    [InlineData("13.1.2.3")] // AWS
    [InlineData("18.1.2.3")] // AWS
    [InlineData("52.1.2.3")] // AWS
    public async Task DetectAsync_AwsIp_ReturnsElevatedConfidence(string ipAddress)
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress(ipAddress);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3, $"AWS IP {ipAddress} should have elevated confidence");
        Assert.Contains(result.Reasons, r => r.Category == "IP");
    }

    [Theory]
    [InlineData("20.1.2.3")] // Azure
    [InlineData("40.1.2.3")] // Azure
    [InlineData("104.1.2.3")] // Azure
    public async Task DetectAsync_AzureIp_ReturnsElevatedConfidence(string ipAddress)
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress(ipAddress);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3, $"Azure IP {ipAddress} should have elevated confidence");
    }

    [Theory]
    [InlineData("34.1.2.3")] // GCP
    [InlineData("35.1.2.3")] // GCP
    public async Task DetectAsync_GcpIp_ReturnsElevatedConfidence(string ipAddress)
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress(ipAddress);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3, $"GCP IP {ipAddress} should have elevated confidence");
    }

    [Theory]
    [InlineData("138.1.2.3")] // Oracle Cloud
    [InlineData("139.1.2.3")] // Oracle Cloud
    [InlineData("140.1.2.3")] // Oracle Cloud
    public async Task DetectAsync_OracleCloudIp_ReturnsElevatedConfidence(string ipAddress)
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress(ipAddress);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3, $"Oracle Cloud IP {ipAddress} should have elevated confidence");
    }

    #endregion

    #region Residential IP Tests

    [Theory]
    [InlineData("192.168.1.1")] // Private
    [InlineData("10.0.0.1")] // Private
    [InlineData("172.16.0.1")] // Private
    [InlineData("98.45.67.89")] // Typical residential
    [InlineData("76.123.45.67")] // Typical residential
    public async Task DetectAsync_NonDatacenterIp_ReturnsLowConfidence(string ipAddress)
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress(ipAddress);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence <= 0.3, $"Residential IP {ipAddress} should have low confidence");
    }

    [Fact]
    public async Task DetectAsync_LocalhostIp_ReturnsLowConfidence()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress("127.0.0.1");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence <= 0.1, "Localhost should have very low confidence");
    }

    #endregion

    #region X-Forwarded-For Header Tests

    [Fact]
    public async Task DetectAsync_XForwardedFor_UsesFirstIp()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = "52.1.2.3, 10.0.0.1, 192.168.1.1"
        });

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3, "Should use first IP from X-Forwarded-For (AWS)");
    }

    [Fact]
    public async Task DetectAsync_XForwardedForSingleIp_Works()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = "34.1.2.3"
        });

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.3, "Should detect GCP IP from X-Forwarded-For");
    }

    [Fact]
    public async Task DetectAsync_InvalidXForwardedFor_FallsBackToRemoteIp()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithHeaders(new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = "not-an-ip"
        });
        context.Connection.RemoteIpAddress = IPAddress.Parse("98.45.67.89");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - should fallback to remote IP which is residential
        Assert.True(result.Confidence <= 0.3);
    }

    #endregion

    #region Cloud Provider Detection Tests

    [Fact]
    public async Task DetectAsync_AwsIp_IdentifiesAsAws()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress("52.1.2.3");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("AWS", StringComparison.OrdinalIgnoreCase) ||
            r.Detail.Contains("cloud provider", StringComparison.OrdinalIgnoreCase) ||
            r.Detail.Contains("datacenter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_AzureIp_IdentifiesAsAzure()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress("20.1.2.3");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("Azure", StringComparison.OrdinalIgnoreCase) ||
            r.Detail.Contains("cloud provider", StringComparison.OrdinalIgnoreCase) ||
            r.Detail.Contains("datacenter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_GcpIp_IdentifiesAsGcp()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress("34.1.2.3");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r =>
            r.Detail.Contains("Google", StringComparison.OrdinalIgnoreCase) ||
            r.Detail.Contains("cloud provider", StringComparison.OrdinalIgnoreCase) ||
            r.Detail.Contains("datacenter", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Confidence Bounds Tests

    [Fact]
    public async Task DetectAsync_ConfidenceNeverExceedsOne()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress("3.1.2.3"); // AWS

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence <= 1.0, "Confidence should never exceed 1.0");
    }

    [Fact]
    public async Task DetectAsync_ConfidenceNeverNegative()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.1");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.True(result.Confidence >= 0.0, "Confidence should never be negative");
    }

    #endregion

    #region Custom Options Tests

    [Fact]
    public async Task DetectAsync_EmptyDatacenterPrefixes_NoDetection()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            DatacenterIpPrefixes = new List<string>()
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithIpAddress("52.1.2.3");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - Should only detect cloud provider, not datacenter range
        Assert.DoesNotContain(result.Reasons, r => r.Detail.Contains("datacenter range"));
    }

    [Fact]
    public async Task DetectAsync_CustomDatacenterPrefix_Detects()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            DatacenterIpPrefixes = new List<string> { "192.168.0.0/16" }
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithIpAddress("192.168.1.100");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Contains(result.Reasons, r => r.Detail.Contains("datacenter"));
    }

    #endregion

    #region Reason Validation Tests

    [Fact]
    public async Task DetectAsync_AllReasonsHaveIpCategory()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress("52.1.2.3");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        foreach (var reason in result.Reasons) Assert.Equal("IP", reason.Category);
    }

    [Fact]
    public async Task DetectAsync_AllReasonsHaveValidConfidenceImpact()
    {
        // Arrange
        var detector = CreateDetector();
        var context = MockHttpContext.CreateWithIpAddress("52.1.2.3");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        foreach (var reason in result.Reasons)
            Assert.True(reason.ConfidenceImpact >= 0.0 && reason.ConfidenceImpact <= 1.0);
    }

    #endregion
}