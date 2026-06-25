using Mostlylucid.BotDetection.Policies.Rules;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies;

public sealed class PolicyActionIntentTests
{
    [Fact] public void Allow_Intent_Allow()     => Assert.Equal(PolicyIntentKind.Allow,     new PolicyAction.Allow().Intent);
    [Fact] public void Observe_Intent_Observe() => Assert.Equal(PolicyIntentKind.Observe,   new PolicyAction.Observe().Intent);
    [Fact] public void Block_Intent_Block()     => Assert.Equal(PolicyIntentKind.Block,     new PolicyAction.Block().Intent);
    [Fact] public void Tag_Intent_Tag()         => Assert.Equal(PolicyIntentKind.Tag,       new PolicyAction.Tag("suspicious").Intent);
    [Fact] public void Challenge_Intent_Challenge() => Assert.Equal(PolicyIntentKind.Challenge, new PolicyAction.Challenge("hcaptcha").Intent);
    [Fact] public void RateLimit_Intent_Throttle() => Assert.Equal(PolicyIntentKind.Throttle, new PolicyAction.RateLimit(60).Intent);
    [Fact] public void Throttle_Intent_Throttle()  => Assert.Equal(PolicyIntentKind.Throttle, new PolicyAction.Throttle(10, null).Intent);
}