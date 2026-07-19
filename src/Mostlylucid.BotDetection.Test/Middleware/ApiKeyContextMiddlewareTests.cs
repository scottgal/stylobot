using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Middleware;

public sealed class ApiKeyContextMiddlewareTests
{
    [Fact]
    public async Task Valid_key_for_public_path_sets_context_before_next_delegate()
    {
        var options = Options.Create(OptionsWithKey());
        var store = new InMemoryApiKeyStore(options, NullLogger<InMemoryApiKeyStore>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/dashboard/policies";
        context.Request.Headers["X-SB-Api-Key"] = "test-public-key";

        ApiKeyContext? observed = null;
        var middleware = new ApiKeyContextMiddleware(ctx =>
        {
            observed = ctx.Items[ApiKeyContextMiddleware.ContextItemKey] as ApiKeyContext;
            return Task.CompletedTask;
        }, store, options);

        await middleware.InvokeAsync(context);

        Assert.NotNull(observed);
        Assert.Equal("staging verifier", observed!.KeyName);
        Assert.Equal("logonly", observed.ActionPolicyName);
    }

    [Fact]
    public async Task Valid_key_denied_for_path_does_not_set_context()
    {
        var options = Options.Create(OptionsWithKey());
        var store = new InMemoryApiKeyStore(options, NullLogger<InMemoryApiKeyStore>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/alive";
        context.Request.Headers["X-SB-Api-Key"] = "test-public-key";

        var middleware = new ApiKeyContextMiddleware(_ => Task.CompletedTask, store, options);

        await middleware.InvokeAsync(context);

        Assert.False(context.Items.ContainsKey(ApiKeyContextMiddleware.ContextItemKey));
    }

    private static BotDetectionOptions OptionsWithKey() => new()
    {
        ApiKeys = new Dictionary<string, ApiKeyConfig>
        {
            ["test-public-key"] = new()
            {
                Name = "staging verifier",
                AllowedPaths = ["/dashboard/**"],
                ActionPolicyName = "logonly"
            }
        }
    };
}
