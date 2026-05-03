using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Llm;
using Mostlylucid.BotDetection.Llm.Holodeck;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class LlmHolodeckResponderTests
{
    private static readonly PackResponseTemplate DynamicTemplate = new()
    {
        PathPattern = "/wp-login.php", Body = "static fallback with {{nonce}} placeholder",
        Dynamic = true, ContentType = "text/html",
        ResponseHints = new PackResponseHints { EndpointDescription = "WordPress login page", ResponseFormat = "html" }
    };

    private static readonly HolodeckRequestContext TestContext = new()
    {
        Method = "GET", Path = "/wp-login.php", PackFramework = "WordPress", PackVersion = "5.9", Fingerprint = "test-fp"
    };

    [Fact]
    public async Task GenerateAsync_CallsLlmProvider()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(true);
        mockLlm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html><body><h1>WordPress Login</h1></body></html>");

        var responder = CreateResponder(mockLlm.Object);
        var response = await responder.GenerateAsync(DynamicTemplate, TestContext, "abc12345");

        Assert.True(response.WasGenerated);
        Assert.Contains("WordPress Login", response.Content);
        Assert.Equal("text/html", response.ContentType);
        mockLlm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_CachesResponse()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(true);
        mockLlm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html>generated</html>");

        var responder = CreateResponder(mockLlm.Object);
        await responder.GenerateAsync(DynamicTemplate, TestContext, "abc12345");
        await responder.GenerateAsync(DynamicTemplate, TestContext, "abc12345");

        mockLlm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_FallsBackOnLlmFailure()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(true);
        mockLlm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timeout"));

        var responder = CreateResponder(mockLlm.Object);
        var response = await responder.GenerateAsync(DynamicTemplate, TestContext, "abc12345");

        Assert.False(response.WasGenerated);
        Assert.Contains("abc12345", response.Content);
    }

    [Fact]
    public async Task GenerateAsync_FallsBackWhenLlmNotReady()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(false);

        var responder = CreateResponder(mockLlm.Object);
        var response = await responder.GenerateAsync(DynamicTemplate, TestContext, null);

        Assert.False(response.WasGenerated);
        Assert.Contains("static fallback", response.Content);
    }

    [Fact]
    public void IsAvailable_ReflectsLlmState()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(true);
        Assert.True(CreateResponder(mockLlm.Object).IsAvailable);

        mockLlm.Setup(l => l.IsReady).Returns(false);
        Assert.False(CreateResponder(mockLlm.Object).IsAvailable);
    }

    private static LlmHolodeckResponder CreateResponder(ILlmProvider llm)
    {
        var options = Options.Create(new HolodeckLlmOptions { TimeoutMs = 3000, CacheSize = 100, CacheTtlHours = 24 });
        return new LlmHolodeckResponder(llm, options, NullLogger<LlmHolodeckResponder>.Instance);
    }
}
