using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Test.Helpers;

namespace Mostlylucid.BotDetection.Test.Detectors;

/// <summary>
///     Tests for LlmDetector configuration and enable/disable behavior
/// </summary>
public class LlmDetectorTests : IDisposable
{
    private readonly ILogger<LlmDetector> _logger;

    public LlmDetectorTests()
    {
        _logger = new Mock<ILogger<LlmDetector>>().Object;
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    private LlmDetector CreateDetector(BotDetectionOptions? options = null)
    {
        return new LlmDetector(
            _logger,
            Options.Create(options ?? new BotDetectionOptions()));
    }

    #region Configuration Defaults Tests

    [Fact]
    public void OllamaOptions_DefaultsAreCorrect()
    {
        // Arrange
        var options = new OllamaOptions();

        // Assert
        Assert.True(options.Enabled);
        Assert.Equal("http://localhost:11434", options.Endpoint);
        Assert.Equal("gemma3:4b", options.Model);
        Assert.True(options.UseJsonMode);
    }

    #endregion

    #region Enabled Configuration Tests

    [Fact]
    public async Task DetectAsync_WhenGloballyDisabled_ReturnsEmptyResult()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            EnableLlmDetection = false,
            AiDetection = new AiDetectionOptions
            {
                Ollama = new OllamaOptions { Enabled = true }
            }
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithUserAgent("Mozilla/5.0 Chrome/120");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Equal(0, result.Confidence);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public async Task DetectAsync_WhenOllamaDisabled_ReturnsEmptyResult()
    {
        // Arrange
        var options = new BotDetectionOptions
        {
            EnableLlmDetection = true,
            AiDetection = new AiDetectionOptions
            {
                Ollama = new OllamaOptions { Enabled = false }
            }
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithUserAgent("Mozilla/5.0 Chrome/120");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Assert.Equal(0, result.Confidence);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public async Task DetectAsync_WhenBothEnabled_ButNoOllamaRunning_ReturnsEmptyOrFails()
    {
        // Arrange - Ollama is enabled but not running on localhost
        var options = new BotDetectionOptions
        {
            EnableLlmDetection = true,
            AiDetection = new AiDetectionOptions
            {
                TimeoutMs = 100, // Short timeout to fail fast
                Ollama = new OllamaOptions
                {
                    Enabled = true,
                    Endpoint = "http://localhost:99999" // Non-existent endpoint
                }
            }
        };
        var detector = CreateDetector(options);
        var context = MockHttpContext.CreateWithUserAgent("Mozilla/5.0 Chrome/120");

        // Act - should not throw, just return empty result due to timeout/connection failure
        var result = await detector.DetectAsync(context);

        // Assert - should gracefully handle the connection failure
        Assert.NotNull(result);
    }

    #endregion
}