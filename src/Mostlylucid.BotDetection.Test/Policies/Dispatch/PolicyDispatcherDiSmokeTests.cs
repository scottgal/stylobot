using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.Dispatch;

/// <summary>
///     Pegs the detection-only DI contract: a host that calls AddBotDetection
///     and nothing else must be able to resolve PolicyActionDispatcher. Before
///     b4a925e6 the dispatcher's IPolicyResolver dependency was only satisfied
///     by AddStyloBotDashboard, and any Demo / BDF rig / minimal-config test
///     that didn't pull in the dashboard crashed at boot with "Unable to
///     resolve service for type IPolicyResolver while attempting to activate
///     PolicyActionDispatcher". The bug took 40 integration tests down in
///     cascade via the Demo app failing ValidateOnBuild.
///     <para>
///         If this test fails in future, the most likely root cause is
///         AddPolicyDispatcher having dropped its TryAddSingleton for
///         IPolicyResolver / IPolicyRuleStore. Restoring them in
///         <c>PolicyDispatchServiceExtensions</c> fixes both this test and
///         the cascading integration failures.
///     </para>
/// </summary>
public class PolicyDispatcherDiSmokeTests
{
    [Fact]
    public void AddPolicyDispatcher_alone_resolves_dispatcher_with_real_resolver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPolicyDispatcher();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var dispatcher = provider.GetRequiredService<PolicyActionDispatcher>();
        Assert.NotNull(dispatcher);

        var resolver = provider.GetRequiredService<IPolicyResolver>();
        Assert.IsType<DefaultPolicyResolver>(resolver);

        var store = provider.GetRequiredService<IPolicyRuleStore>();
        Assert.IsType<YamlPolicyRuleStore>(store);
    }

    [Fact]
    public void AddPolicyDispatcher_is_idempotent_for_resolver_and_store()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPolicyDispatcher();
        services.AddPolicyDispatcher();

        using var provider = services.BuildServiceProvider();

        var dispatchers = provider.GetServices<PolicyActionDispatcher>().ToList();
        Assert.Single(dispatchers);

        var resolvers = provider.GetServices<IPolicyResolver>().ToList();
        Assert.Single(resolvers);
    }
}
