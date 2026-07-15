using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Services;
using Moq;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     Regression coverage for the commercial-website factory <c>ValidateOnBuild</c> failure:
///     <c>EffectivePolicyComposer</c> (Singleton) could not resolve <c>IActionPolicyRegistry</c> on
///     the remote / thin-client dashboard host.
///
///     <para>
///     <see cref="EffectivePolicyComposer"/> composes the config baseline
///     (<c>BotTypeActionPolicies</c> -> real <c>ActionType</c>) from the local
///     <see cref="IActionPolicyRegistry"/>, which only <c>AddBotDetection</c> registers. A remote /
///     thin-client dashboard (Stylobot.Ui rest mode, the commercial website factory) runs
///     <c>AddStyloBotDashboard</c> WITHOUT <c>AddBotDetection</c>, so the registry is absent and an
///     unconditional composer registration failed <c>ValidateOnBuild</c>.
///     </para>
///
///     <para>
///     The fix registers the composer ONLY where its real dependency exists. It deliberately does
///     NOT fabricate a null / empty <see cref="IActionPolicyRegistry"/>: an empty registry would
///     render a wrong (blank / mis-coloured) effective policy on a thin client -- the null-object
///     degradation class. Remote surfaces the gateway's baseline via the read path; the config-baseline
///     view is gated on this same registration.
///     </para>
/// </summary>
public class EffectivePolicyComposerRemoteDiTests
{
    [Fact]
    public void Composer_is_NOT_registered_when_action_policy_registry_absent()
    {
        // Remote / thin-client host: AddStyloBotDashboard WITHOUT AddBotDetection -> no registry.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddStyloBotDashboard();

        services.Any(sd => sd.ServiceType == typeof(EffectivePolicyComposer)).Should().BeFalse(
            "the composer hard-depends on IActionPolicyRegistry (local detection only); registering it " +
            "on a registry-less host is exactly the ValidateOnBuild failure this guard prevents");
    }

    [Fact]
    public void Composer_IS_registered_when_action_policy_registry_present()
    {
        // Local-detection host: AddBotDetection has registered IActionPolicyRegistry before the
        // dashboard wiring runs (AddStyloBot calls AddBotDetection first).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IActionPolicyRegistry>());

        services.AddStyloBotDashboard();

        services.Any(sd => sd.ServiceType == typeof(EffectivePolicyComposer)).Should().BeTrue(
            "where the real registry exists (local detection), the config-baseline composer must be available");
    }
}
