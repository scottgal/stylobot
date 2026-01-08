using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Models;

/// <summary>
///     Tests for BotDetectionOptions default values and validation
/// </summary>
public class BotDetectionOptionsTests
{
    #region Default Values Tests

    [Fact]
    public void Constructor_SetsDefaultBotThreshold()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.Equal(0.7, options.BotThreshold);
    }

    [Fact]
    public void Constructor_SetsDefaultTestModeDisabled()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.False(options.EnableTestMode);
    }

    [Fact]
    public void Constructor_SetsDefaultUserAgentDetectionEnabled()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.True(options.EnableUserAgentDetection);
    }

    [Fact]
    public void Constructor_SetsDefaultHeaderAnalysisEnabled()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.True(options.EnableHeaderAnalysis);
    }

    [Fact]
    public void Constructor_SetsDefaultIpDetectionEnabled()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.True(options.EnableIpDetection);
    }

    [Fact]
    public void Constructor_SetsDefaultBehavioralAnalysisEnabled()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.True(options.EnableBehavioralAnalysis);
    }

    [Fact]
    public void Constructor_SetsDefaultLlmDetectionDisabled()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.False(options.EnableLlmDetection);
    }

    [Fact]
    public void Constructor_SetsDefaultOllamaEndpoint()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.Equal("http://localhost:11434", options.OllamaEndpoint);
    }

    [Fact]
    public void Constructor_SetsDefaultOllamaModel()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert - Default model was changed to gemma3:4b (better reasoning, 8K context)
        Assert.Equal("gemma3:4b", options.OllamaModel);
    }

    [Fact]
    public void Constructor_SetsDefaultLlmTimeout()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert - Timeout increased to 15000ms for larger 4b model and cold start
        Assert.Equal(15000, options.LlmTimeoutMs);
    }

    [Fact]
    public void Constructor_SetsDefaultMaxRequestsPerMinute()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.Equal(60, options.MaxRequestsPerMinute);
    }

    [Fact]
    public void Constructor_SetsDefaultCacheDuration()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.Equal(300, options.CacheDurationSeconds);
    }

    #endregion

    #region Whitelisted Bot Patterns Tests

    [Fact]
    public void Constructor_InitializesWhitelistedBotPatterns()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.NotNull(options.WhitelistedBotPatterns);
        Assert.NotEmpty(options.WhitelistedBotPatterns);
    }

    [Theory]
    [InlineData("Googlebot")]
    [InlineData("Bingbot")]
    [InlineData("DuckDuckBot")]
    [InlineData("Slackbot")]
    [InlineData("Baiduspider")]
    [InlineData("YandexBot")]
    public void Constructor_ContainsCommonGoodBots(string botName)
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.Contains(botName, options.WhitelistedBotPatterns);
    }

    [Fact]
    public void WhitelistedBotPatterns_CanBeModified()
    {
        // Arrange
        var options = new BotDetectionOptions();
        var customBot = "MyCustomBot";

        // Act
        options.WhitelistedBotPatterns.Add(customBot);

        // Assert
        Assert.Contains(customBot, options.WhitelistedBotPatterns);
    }

    [Fact]
    public void WhitelistedBotPatterns_CanBeCleared()
    {
        // Arrange
        var options = new BotDetectionOptions();

        // Act
        options.WhitelistedBotPatterns.Clear();

        // Assert
        Assert.Empty(options.WhitelistedBotPatterns);
    }

    [Fact]
    public void WhitelistedBotPatterns_CanBeReplaced()
    {
        // Arrange
        var options = new BotDetectionOptions();
        var customBots = new List<string> { "CustomBot1", "CustomBot2" };

        // Act
        options.WhitelistedBotPatterns = customBots;

        // Assert
        Assert.Equal(2, options.WhitelistedBotPatterns.Count);
        Assert.Contains("CustomBot1", options.WhitelistedBotPatterns);
        Assert.Contains("CustomBot2", options.WhitelistedBotPatterns);
    }

    #endregion

    #region Datacenter IP Prefixes Tests

    [Fact]
    public void Constructor_InitializesDatacenterIpPrefixes()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.NotNull(options.DatacenterIpPrefixes);
        Assert.NotEmpty(options.DatacenterIpPrefixes);
    }

    [Fact]
    public void Constructor_ContainsAwsIpPrefixes()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert - Check for AWS prefixes
        Assert.Contains(options.DatacenterIpPrefixes, p => p.StartsWith("3."));
        Assert.Contains(options.DatacenterIpPrefixes, p => p.StartsWith("13."));
        Assert.Contains(options.DatacenterIpPrefixes, p => p.StartsWith("52."));
    }

    [Fact]
    public void Constructor_ContainsAzureIpPrefixes()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert - Check for Azure prefixes
        Assert.Contains(options.DatacenterIpPrefixes, p => p.StartsWith("20."));
        Assert.Contains(options.DatacenterIpPrefixes, p => p.StartsWith("40."));
    }

    [Fact]
    public void Constructor_ContainsGcpIpPrefixes()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert - Check for GCP prefixes
        Assert.Contains(options.DatacenterIpPrefixes, p => p.StartsWith("34."));
        Assert.Contains(options.DatacenterIpPrefixes, p => p.StartsWith("35."));
    }

    [Fact]
    public void DatacenterIpPrefixes_CanBeModified()
    {
        // Arrange
        var options = new BotDetectionOptions();
        var customPrefix = "100.0.0.0/8";

        // Act
        options.DatacenterIpPrefixes.Add(customPrefix);

        // Assert
        Assert.Contains(customPrefix, options.DatacenterIpPrefixes);
    }

    #endregion

    #region Property Setting Tests

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(0.9)]
    public void BotThreshold_CanBeSet(double threshold)
    {
        // Arrange
        var options = new BotDetectionOptions();

        // Act
        options.BotThreshold = threshold;

        // Assert
        Assert.Equal(threshold, options.BotThreshold);
    }

    [Fact]
    public void EnableTestMode_CanBeEnabled()
    {
        // Arrange
        var options = new BotDetectionOptions();

        // Act
        options.EnableTestMode = true;

        // Assert
        Assert.True(options.EnableTestMode);
    }

    [Fact]
    public void OllamaEndpoint_CanBeSet()
    {
        // Arrange
        var options = new BotDetectionOptions();
        var customEndpoint = "http://custom-ollama:11434";

        // Act
        options.OllamaEndpoint = customEndpoint;

        // Assert
        Assert.Equal(customEndpoint, options.OllamaEndpoint);
    }

    [Fact]
    public void OllamaModel_CanBeSet()
    {
        // Arrange
        var options = new BotDetectionOptions();
        var customModel = "llama3.2:latest";

        // Act
        options.OllamaModel = customModel;

        // Assert
        Assert.Equal(customModel, options.OllamaModel);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public void LlmTimeoutMs_CanBeSet(int timeout)
    {
        // Arrange
        var options = new BotDetectionOptions();

        // Act
        options.LlmTimeoutMs = timeout;

        // Assert
        Assert.Equal(timeout, options.LlmTimeoutMs);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(100)]
    [InlineData(1000)]
    public void MaxRequestsPerMinute_CanBeSet(int maxRequests)
    {
        // Arrange
        var options = new BotDetectionOptions();

        // Act
        options.MaxRequestsPerMinute = maxRequests;

        // Assert
        Assert.Equal(maxRequests, options.MaxRequestsPerMinute);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(600)]
    [InlineData(3600)]
    public void CacheDurationSeconds_CanBeSet(int duration)
    {
        // Arrange
        var options = new BotDetectionOptions();

        // Act
        options.CacheDurationSeconds = duration;

        // Assert
        Assert.Equal(duration, options.CacheDurationSeconds);
    }

    #endregion

    #region All Detectors Disabled Tests

    [Fact]
    public void AllDetectors_CanBeDisabled()
    {
        // Arrange & Act
        var options = new BotDetectionOptions
        {
            EnableUserAgentDetection = false,
            EnableHeaderAnalysis = false,
            EnableIpDetection = false,
            EnableBehavioralAnalysis = false,
            EnableLlmDetection = false
        };

        // Assert
        Assert.False(options.EnableUserAgentDetection);
        Assert.False(options.EnableHeaderAnalysis);
        Assert.False(options.EnableIpDetection);
        Assert.False(options.EnableBehavioralAnalysis);
        Assert.False(options.EnableLlmDetection);
    }

    [Fact]
    public void AllDetectors_CanBeEnabled()
    {
        // Arrange & Act
        var options = new BotDetectionOptions
        {
            EnableUserAgentDetection = true,
            EnableHeaderAnalysis = true,
            EnableIpDetection = true,
            EnableBehavioralAnalysis = true,
            EnableLlmDetection = true
        };

        // Assert
        Assert.True(options.EnableUserAgentDetection);
        Assert.True(options.EnableHeaderAnalysis);
        Assert.True(options.EnableIpDetection);
        Assert.True(options.EnableBehavioralAnalysis);
        Assert.True(options.EnableLlmDetection);
    }

    #endregion

    #region BehavioralOptions Tests

    [Fact]
    public void Constructor_InitializesBehavioralOptions()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.NotNull(options.Behavioral);
    }

    [Fact]
    public void BehavioralOptions_DefaultApiKeyHeaderIsNull()
    {
        // Act
        var options = new BehavioralOptions();

        // Assert
        Assert.Null(options.ApiKeyHeader);
    }

    [Fact]
    public void BehavioralOptions_DefaultApiKeyRateLimitIsZero()
    {
        // Act
        var options = new BehavioralOptions();

        // Assert
        Assert.Equal(0, options.ApiKeyRateLimit);
    }

    [Fact]
    public void BehavioralOptions_DefaultUserIdClaimIsNull()
    {
        // Act
        var options = new BehavioralOptions();

        // Assert
        Assert.Null(options.UserIdClaim);
    }

    [Fact]
    public void BehavioralOptions_DefaultUserIdHeaderIsNull()
    {
        // Act
        var options = new BehavioralOptions();

        // Assert
        Assert.Null(options.UserIdHeader);
    }

    [Fact]
    public void BehavioralOptions_DefaultUserRateLimitIsZero()
    {
        // Act
        var options = new BehavioralOptions();

        // Assert
        Assert.Equal(0, options.UserRateLimit);
    }

    [Fact]
    public void BehavioralOptions_DefaultEnableAnomalyDetectionIsTrue()
    {
        // Act
        var options = new BehavioralOptions();

        // Assert
        Assert.True(options.EnableAnomalyDetection);
    }

    [Fact]
    public void BehavioralOptions_DefaultSpikeThresholdMultiplierIsFive()
    {
        // Act
        var options = new BehavioralOptions();

        // Assert
        Assert.Equal(5.0, options.SpikeThresholdMultiplier);
    }

    [Fact]
    public void BehavioralOptions_DefaultNewPathAnomalyThresholdIsPointEight()
    {
        // Act
        var options = new BehavioralOptions();

        // Assert
        Assert.Equal(0.8, options.NewPathAnomalyThreshold);
    }

    [Fact]
    public void BehavioralOptions_ApiKeyHeader_CanBeSet()
    {
        // Arrange
        var options = new BehavioralOptions();

        // Act
        options.ApiKeyHeader = "X-Api-Key";

        // Assert
        Assert.Equal("X-Api-Key", options.ApiKeyHeader);
    }

    [Fact]
    public void BehavioralOptions_ApiKeyRateLimit_CanBeSet()
    {
        // Arrange
        var options = new BehavioralOptions();

        // Act
        options.ApiKeyRateLimit = 100;

        // Assert
        Assert.Equal(100, options.ApiKeyRateLimit);
    }

    [Fact]
    public void BehavioralOptions_UserIdClaim_CanBeSet()
    {
        // Arrange
        var options = new BehavioralOptions();

        // Act
        options.UserIdClaim = "sub";

        // Assert
        Assert.Equal("sub", options.UserIdClaim);
    }

    [Fact]
    public void BehavioralOptions_UserIdHeader_CanBeSet()
    {
        // Arrange
        var options = new BehavioralOptions();

        // Act
        options.UserIdHeader = "X-User-Id";

        // Assert
        Assert.Equal("X-User-Id", options.UserIdHeader);
    }

    [Fact]
    public void BehavioralOptions_UserRateLimit_CanBeSet()
    {
        // Arrange
        var options = new BehavioralOptions();

        // Act
        options.UserRateLimit = 200;

        // Assert
        Assert.Equal(200, options.UserRateLimit);
    }

    [Fact]
    public void BehavioralOptions_EnableAnomalyDetection_CanBeDisabled()
    {
        // Arrange
        var options = new BehavioralOptions();

        // Act
        options.EnableAnomalyDetection = false;

        // Assert
        Assert.False(options.EnableAnomalyDetection);
    }

    [Fact]
    public void BehavioralOptions_SpikeThresholdMultiplier_CanBeSet()
    {
        // Arrange
        var options = new BehavioralOptions();

        // Act
        options.SpikeThresholdMultiplier = 10.0;

        // Assert
        Assert.Equal(10.0, options.SpikeThresholdMultiplier);
    }

    [Fact]
    public void BehavioralOptions_NewPathAnomalyThreshold_CanBeSet()
    {
        // Arrange
        var options = new BehavioralOptions();

        // Act
        options.NewPathAnomalyThreshold = 0.5;

        // Assert
        Assert.Equal(0.5, options.NewPathAnomalyThreshold);
    }

    [Fact]
    public void BotDetectionOptions_BehavioralOptions_CanBeConfigured()
    {
        // Arrange & Act
        var options = new BotDetectionOptions
        {
            Behavioral = new BehavioralOptions
            {
                ApiKeyHeader = "Authorization",
                ApiKeyRateLimit = 120,
                UserIdClaim = "sub",
                UserIdHeader = "X-User-Id",
                UserRateLimit = 180,
                EnableAnomalyDetection = true,
                SpikeThresholdMultiplier = 3.0,
                NewPathAnomalyThreshold = 0.7
            }
        };

        // Assert
        Assert.Equal("Authorization", options.Behavioral.ApiKeyHeader);
        Assert.Equal(120, options.Behavioral.ApiKeyRateLimit);
        Assert.Equal("sub", options.Behavioral.UserIdClaim);
        Assert.Equal("X-User-Id", options.Behavioral.UserIdHeader);
        Assert.Equal(180, options.Behavioral.UserRateLimit);
        Assert.True(options.Behavioral.EnableAnomalyDetection);
        Assert.Equal(3.0, options.Behavioral.SpikeThresholdMultiplier);
        Assert.Equal(0.7, options.Behavioral.NewPathAnomalyThreshold);
    }

    #endregion

    #region ClientSideOptions Tests

    [Fact]
    public void Constructor_InitializesClientSideOptions()
    {
        // Act
        var options = new BotDetectionOptions();

        // Assert
        Assert.NotNull(options.ClientSide);
    }

    [Fact]
    public void ClientSideOptions_DefaultEnabledIsFalse()
    {
        // Act
        var options = new ClientSideOptions();

        // Assert
        Assert.False(options.Enabled);
    }

    [Fact]
    public void ClientSideOptions_DefaultTokenSecretIsNull()
    {
        // Act
        var options = new ClientSideOptions();

        // Assert
        Assert.Null(options.TokenSecret);
    }

    [Fact]
    public void ClientSideOptions_DefaultTokenLifetimeIs300()
    {
        // Act
        var options = new ClientSideOptions();

        // Assert
        Assert.Equal(300, options.TokenLifetimeSeconds);
    }

    [Fact]
    public void ClientSideOptions_DefaultFingerprintCacheDurationIs1800()
    {
        // Act
        var options = new ClientSideOptions();

        // Assert
        Assert.Equal(1800, options.FingerprintCacheDurationSeconds);
    }

    [Fact]
    public void ClientSideOptions_DefaultCollectionTimeoutIs5000()
    {
        // Act
        var options = new ClientSideOptions();

        // Assert
        Assert.Equal(5000, options.CollectionTimeoutMs);
    }

    [Fact]
    public void ClientSideOptions_DefaultCollectWebGLIsTrue()
    {
        // Act
        var options = new ClientSideOptions();

        // Assert
        Assert.True(options.CollectWebGL);
    }

    [Fact]
    public void ClientSideOptions_DefaultCollectCanvasIsTrue()
    {
        // Act
        var options = new ClientSideOptions();

        // Assert
        Assert.True(options.CollectCanvas);
    }

    [Fact]
    public void ClientSideOptions_DefaultCollectAudioIsFalse()
    {
        // Act
        var options = new ClientSideOptions();

        // Assert
        Assert.False(options.CollectAudio);
    }

    [Fact]
    public void ClientSideOptions_DefaultMinIntegrityScoreIs70()
    {
        // Act
        var options = new ClientSideOptions();

        // Assert
        Assert.Equal(70, options.MinIntegrityScore);
    }

    [Fact]
    public void ClientSideOptions_DefaultHeadlessThresholdIsPointFive()
    {
        // Act
        var options = new ClientSideOptions();

        // Assert
        Assert.Equal(0.5, options.HeadlessThreshold);
    }

    [Fact]
    public void BotDetectionOptions_ClientSideOptions_CanBeConfigured()
    {
        // Arrange & Act
        var options = new BotDetectionOptions
        {
            ClientSide = new ClientSideOptions
            {
                Enabled = true,
                TokenSecret = "my-secret-key",
                TokenLifetimeSeconds = 600,
                FingerprintCacheDurationSeconds = 3600,
                CollectionTimeoutMs = 10000,
                CollectWebGL = false,
                CollectCanvas = false,
                CollectAudio = true,
                MinIntegrityScore = 80,
                HeadlessThreshold = 0.7
            }
        };

        // Assert
        Assert.True(options.ClientSide.Enabled);
        Assert.Equal("my-secret-key", options.ClientSide.TokenSecret);
        Assert.Equal(600, options.ClientSide.TokenLifetimeSeconds);
        Assert.Equal(3600, options.ClientSide.FingerprintCacheDurationSeconds);
        Assert.Equal(10000, options.ClientSide.CollectionTimeoutMs);
        Assert.False(options.ClientSide.CollectWebGL);
        Assert.False(options.ClientSide.CollectCanvas);
        Assert.True(options.ClientSide.CollectAudio);
        Assert.Equal(80, options.ClientSide.MinIntegrityScore);
        Assert.Equal(0.7, options.ClientSide.HeadlessThreshold);
    }

    #endregion
}