using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Api.Tests.Auth;

public class ApiKeyAuthTests
{
    private const string ValidKey = "SB-TEST-KEY";
    private const string Scheme = ApiKeyAuthenticationHandler.SchemeName;

    [Fact]
    public async Task ValidKey_ReturnsSuccess()
    {
        var handler = CreateHandler(validKey: ValidKey);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-SB-Api-Key"] = ValidKey;

        await handler.InitializeAsync(
            new AuthenticationScheme(Scheme, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("SB-TEST-KEY", result.Principal!.FindFirst("api_key_name")!.Value);
    }

    [Fact]
    public async Task MissingHeader_ReturnsNoResult()
    {
        var handler = CreateHandler(validKey: ValidKey);
        var context = new DefaultHttpContext();

        await handler.InitializeAsync(
            new AuthenticationScheme(Scheme, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task InvalidKey_ReturnsFail()
    {
        var handler = CreateHandler(validKey: ValidKey);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-SB-Api-Key"] = "SB-WRONG-KEY";

        await handler.InitializeAsync(
            new AuthenticationScheme(Scheme, null, typeof(ApiKeyAuthenticationHandler)), context);
        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.None);
    }

    private static ApiKeyAuthenticationHandler CreateHandler(string validKey)
    {
        var store = new Mock<IApiKeyStore>();
        store.Setup(s => s.ValidateKeyWithReason(validKey, It.IsAny<string>()))
            .Returns((
                new ApiKeyValidationResult
                {
                    KeyId = validKey,
                    Context = new Mostlylucid.BotDetection.Models.ApiKeyContext
                    {
                        KeyName = validKey,
                        DisabledDetectors = [],
                        WeightOverrides = new Dictionary<string, double>()
                    }
                },
                (ApiKeyRejection?)null));
        store.Setup(s => s.ValidateKeyWithReason(It.Is<string>(k => k != validKey), It.IsAny<string>()))
            .Returns(((ApiKeyValidationResult?)null, new ApiKeyRejection(ApiKeyRejectionReason.NotFound)));

        var options = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Setup(o => o.Get(It.IsAny<string>())).Returns(new AuthenticationSchemeOptions());

        return new ApiKeyAuthenticationHandler(
            store.Object, options.Object, NullLoggerFactory.Instance, UrlEncoder.Default);
    }
}
