using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class InMemoryApiKeyStoreTests
{
    private static InMemoryApiKeyStore CreateStore(BotDetectionOptions options)
    {
        return new InMemoryApiKeyStore(
            Options.Create(options),
            Mock.Of<ILogger<InMemoryApiKeyStore>>());
    }

    [Fact]
    public void ValidateKeyWithReason_PathWildcardHonorsSegmentBoundaries()
    {
        var options = new BotDetectionOptions();
        options.ApiKeys["SB-TEST"] = new ApiKeyConfig
        {
            Name = "Test Key",
            AllowedPaths = ["/_stylobot/api/**"]
        };

        var store = CreateStore(options);

        var (allowedResult, allowedRejection) = store.ValidateKeyWithReason("SB-TEST", "/_stylobot/api/detections");
        var (deniedResult, deniedRejection) = store.ValidateKeyWithReason("SB-TEST", "/_stylobot/apix/detections");

        Assert.NotNull(allowedResult);
        Assert.Null(allowedRejection);
        Assert.Null(deniedResult);
        Assert.NotNull(deniedRejection);
        Assert.Equal(ApiKeyRejectionReason.PathDenied, deniedRejection.Reason);
    }

    [Fact]
    public void ValidateKeyWithReason_InvalidTimeWindow_FailsClosed()
    {
        var options = new BotDetectionOptions();
        options.ApiKeys["SB-TIME"] = new ApiKeyConfig
        {
            Name = "Time Window Key",
            AllowedTimeWindow = "invalid-window"
        };

        var store = CreateStore(options);
        var (result, rejection) = store.ValidateKeyWithReason("SB-TIME", "/api/test");

        Assert.Null(result);
        Assert.NotNull(rejection);
        Assert.Equal(ApiKeyRejectionReason.OutsideTimeWindow, rejection.Reason);
    }

    [Fact]
    public void ValidateKeyWithReason_SimplePrefixHonorsSegmentBoundaries()
    {
        var options = new BotDetectionOptions();
        options.ApiKeys["SB-PREFIX"] = new ApiKeyConfig
        {
            Name = "Prefix Key",
            AllowedPaths = ["/api"]
        };

        var store = CreateStore(options);

        var (exactResult, _) = store.ValidateKeyWithReason("SB-PREFIX", "/api");
        var (childResult, _) = store.ValidateKeyWithReason("SB-PREFIX", "/api/v1");
        var (prefixBypassResult, prefixBypassRejection) = store.ValidateKeyWithReason("SB-PREFIX", "/apiv1");

        Assert.NotNull(exactResult);
        Assert.NotNull(childResult);
        Assert.Null(prefixBypassResult);
        Assert.NotNull(prefixBypassRejection);
        Assert.Equal(ApiKeyRejectionReason.PathDenied, prefixBypassRejection.Reason);
    }
}

