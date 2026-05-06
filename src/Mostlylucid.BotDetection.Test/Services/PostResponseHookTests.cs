using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class PostResponseHookConcreteTests
{
    [Fact]
    public async Task DegradationAtom_IsPostResponseHook()
    {
        var atom = new DegradationAtom();
        Assert.IsAssignableFrom<IStylobotPostResponseHook>(atom);
        await atom.OnResponseCompletedAsync(
            new ResponseContext(200, 50, "/api/test", "logonly"),
            CancellationToken.None);
        atom.Dispose();
    }

    [Fact]
    public async Task DegradationAtom_Records5xx_ViaHookInterface()
    {
        var atom = new DegradationAtom(windowSeconds: 60, emaAlpha: 1.0);
        var hook = (IStylobotPostResponseHook)atom;

        await hook.OnResponseCompletedAsync(
            new ResponseContext(500, 100, "/api/test", null),
            CancellationToken.None);

        Assert.True(atom.GetSignalValue("response.error_rate_5xx") > 0);
        atom.Dispose();
    }

    [Fact]
    public async Task ReactionPackContext_IsPreActionHook()
    {
        var ctx = new ReactionPackContext();
        Assert.IsAssignableFrom<IStylobotPreActionHook>(ctx);
        var result = await ctx.GetOverridePolicyAsync("/api/test", "throttle", CancellationToken.None);
        Assert.Null(result);
    }
}
