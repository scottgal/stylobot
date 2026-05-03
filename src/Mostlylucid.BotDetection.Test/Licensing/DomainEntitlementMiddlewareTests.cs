using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Licensing;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Licensing;

/// <summary>
///     Behavioural tests for <see cref="DomainEntitlementMiddleware"/> - host resolution
///     order, warn-never-lock guarantees, items-key population.
/// </summary>
public sealed class DomainEntitlementMiddlewareTests
{
    [Fact]
    public async Task PassThrough_WhenValidatorIsNotEnforcing_StashesNothing()
    {
        var validator = new DomainEntitlementValidator(null);
        var ctx = new DefaultHttpContext { Request = { Host = new HostString("example.com") } };
        var nextRan = false;
        var mw = new DomainEntitlementMiddleware(_ => { nextRan = true; return Task.CompletedTask; },
            validator, NullLogger<DomainEntitlementMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        Assert.True(nextRan);
        Assert.False(ctx.Items.ContainsKey(DomainEntitlementMiddleware.ResultItemsKey));
        Assert.False(ctx.Items.ContainsKey(DomainEntitlementMiddleware.HostItemsKey));
    }

    [Fact]
    public async Task LicensedHost_StashesLicensedResult_AndAlwaysCallsNext()
    {
        var validator = new DomainEntitlementValidator(new[] { "acme.com" });
        var ctx = new DefaultHttpContext { Request = { Host = new HostString("api.acme.com") } };
        var nextRan = false;
        var mw = new DomainEntitlementMiddleware(_ => { nextRan = true; return Task.CompletedTask; },
            validator, NullLogger<DomainEntitlementMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        Assert.True(nextRan);
        Assert.Equal(DomainEntitlementResult.Licensed, ctx.Items[DomainEntitlementMiddleware.ResultItemsKey]);
        Assert.Equal("api.acme.com", ctx.Items[DomainEntitlementMiddleware.HostItemsKey]);
    }

    [Fact]
    public async Task MismatchedHost_NeverAffectsResponse_StatusUntouched()
    {
        var validator = new DomainEntitlementValidator(new[] { "acme.com" });
        var ctx = new DefaultHttpContext
        {
            Request = { Host = new HostString("rogue.example.org") },
            Response = { StatusCode = 200 }
        };
        var mw = new DomainEntitlementMiddleware(_ => Task.CompletedTask,
            validator, NullLogger<DomainEntitlementMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(DomainEntitlementResult.Mismatch, ctx.Items[DomainEntitlementMiddleware.ResultItemsKey]);
    }

    [Fact]
    public async Task XForwardedHost_BeatsHostHeader()
    {
        var validator = new DomainEntitlementValidator(new[] { "acme.com" });
        var ctx = new DefaultHttpContext { Request = { Host = new HostString("internal-pod-7.local") } };
        ctx.Request.Headers["X-Forwarded-Host"] = "api.acme.com";
        var mw = new DomainEntitlementMiddleware(_ => Task.CompletedTask,
            validator, NullLogger<DomainEntitlementMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        // Effective host should be the forwarded value, not the internal pod.
        Assert.Equal("api.acme.com", ctx.Items[DomainEntitlementMiddleware.HostItemsKey]);
        Assert.Equal(DomainEntitlementResult.Licensed, ctx.Items[DomainEntitlementMiddleware.ResultItemsKey]);
    }

    [Fact]
    public async Task XForwardedHost_TakesFirstValue_WhenChained()
    {
        var validator = new DomainEntitlementValidator(new[] { "acme.com" });
        var ctx = new DefaultHttpContext();
        // Most-recent proxy first per common reverse proxy convention.
        ctx.Request.Headers["X-Forwarded-Host"] = "api.acme.com, edge.cloudfront.net, origin.lb";
        var mw = new DomainEntitlementMiddleware(_ => Task.CompletedTask,
            validator, NullLogger<DomainEntitlementMiddleware>.Instance);

        await mw.InvokeAsync(ctx);

        Assert.Equal("api.acme.com", ctx.Items[DomainEntitlementMiddleware.HostItemsKey]);
        Assert.Equal(DomainEntitlementResult.Licensed, ctx.Items[DomainEntitlementMiddleware.ResultItemsKey]);
    }
}