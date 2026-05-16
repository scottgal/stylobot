using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Sidecar;
using Mostlylucid.BotDetection.Sidecar.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Sidecar;

/// <summary>
///     Covers the sidecar's gRPC authentication decision and the deployment-time
///     bounds that protect the engine from caller-supplied input.
/// </summary>
public class SidecarSecurityTests
{
    private const string GrpcMethod = "/stylobot.detection.v1.DetectionService/Detect";

    private static ApiKeyGrpcAuthenticator BuildAuthenticator(params string[] keys)
    {
        var botOptions = new BotDetectionOptions();
        foreach (var key in keys)
            botOptions.ApiKeys[key] = new ApiKeyConfig { Name = key };

        var wrapped = Options.Create(botOptions);
        var store = new InMemoryApiKeyStore(wrapped, NullLogger<InMemoryApiKeyStore>.Instance);
        return new ApiKeyGrpcAuthenticator(store, wrapped);
    }

    [Fact]
    public void KeysConfigured_False_WhenNoKeys()
    {
        Assert.False(BuildAuthenticator().KeysConfigured);
    }

    [Fact]
    public void KeysConfigured_True_WhenKeyPresent()
    {
        Assert.True(BuildAuthenticator("secret-key").KeysConfigured);
    }

    [Fact]
    public void Validate_Accepts_CorrectKey()
    {
        var (ok, error) = BuildAuthenticator("secret-key").Validate("secret-key", GrpcMethod);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_Rejects_WrongKey()
    {
        var (ok, error) = BuildAuthenticator("secret-key").Validate("not-the-key", GrpcMethod);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_Rejects_MissingKey(string? presented)
    {
        var (ok, error) = BuildAuthenticator("secret-key").Validate(presented, GrpcMethod);
        Assert.False(ok);
        Assert.Contains("missing", error);
    }

    [Fact]
    public void SidecarOptions_Defaults_AreSecureAndBounded()
    {
        var options = new SidecarOptions();

        Assert.False(options.BindAnyInterface);   // loopback by default
        Assert.False(options.AllowInsecure);
        Assert.Equal(100, options.MaxBatchSize);
        Assert.Equal(64 * 1024, options.MaxTemplateLength);
        Assert.Equal(10_000, options.MaxRenderSteps);
    }
}
