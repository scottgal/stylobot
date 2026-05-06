using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class PreActionHookTests
{
    [Fact]
    public async Task Hook_ReturnsNull_WhenNoOverride()
    {
        var hook = new NullPreActionHook();
        var result = await hook.GetOverridePolicyAsync("/api/test", "throttle", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task Hook_ReturnsPolicy_WhenOverrideAvailable()
    {
        var hook = new FixedPolicyHook("block");
        var result = await hook.GetOverridePolicyAsync("/api/test", "throttle", CancellationToken.None);
        Assert.Equal("block", result);
    }

    private sealed class NullPreActionHook : IStylobotPreActionHook
    {
        public int Priority => 0;
        public ValueTask<string?> GetOverridePolicyAsync(string endpoint, string currentPolicy, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);
    }

    private sealed class FixedPolicyHook(string policy) : IStylobotPreActionHook
    {
        public int Priority => 0;
        public ValueTask<string?> GetOverridePolicyAsync(string endpoint, string currentPolicy, CancellationToken ct)
            => ValueTask.FromResult<string?>(policy);
    }
}
