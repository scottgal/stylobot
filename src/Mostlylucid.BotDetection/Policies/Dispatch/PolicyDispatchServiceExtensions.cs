using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Dispatch.Handlers;
using Mostlylucid.BotDetection.Policies.Triggers;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Policies.Dispatch;

/// <summary>
///     DI registration for the policy-action dispatcher and its per-action
///     handlers. Called from <c>AddBotDetection</c> so every FOSS host
///     automatically gets the dispatcher wiring without an extra opt-in
///     step; commercial packs replace individual handlers / the resolver
///     via TryAdd-loses semantics.
/// </summary>
public static class PolicyDispatchServiceExtensions
{
    /// <summary>
    ///     Register <see cref="PolicyActionDispatcher"/>, every built-in
    ///     <see cref="IPolicyActionHandler"/>, and the supporting Phase G
    ///     primitives (<see cref="ArmedRuleRegistry"/>,
    ///     <see cref="ITokenBucketStore"/>). All registrations use
    ///     <c>TryAdd</c> so a host that pre-registered any of these keeps
    ///     its own bindings. Idempotent.
    /// </summary>
    public static IServiceCollection AddPolicyDispatcher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Phase G primitives the dispatcher and resolver depend on. The
        // dashboard DI extension also registers these via TryAdd; calling
        // both is safe.
        services.TryAddSingleton<ArmedRuleRegistry>();
        // ITokenBucketStore is the single token-bucket primitive used by
        // both RateLimit and Throttle handlers (Wave 3 consolidation --
        // see feedback_no_duplication). Hosts may replace the in-memory
        // default with a SQLite or distributed implementation; the FOSS
        // default keeps tests boot-time free.
        services.TryAddSingleton<ITokenBucketStore, InMemoryTokenBucketStore>();

        // Wave 4: PolicyDecisionLogQueue is the write-behind shim in front of
        // IPolicyDecisionLog. The dispatcher enqueues here on the request hot
        // path; a Tick1s drainer copies into the durable log. Reuses the same
        // bounded-channel + drainer pattern as PolicyEffectivenessCache.
        // Only registered if BOTH an IPolicyDecisionLog and an IScheduleCoordinator
        // are available -- a viewer host with neither (e.g. dashboard-only)
        // resolves null and the dispatcher's TryLogDecisionAsync falls back to
        // the structured-log line.
        services.TryAddSingleton<IPolicyDecisionLogQueue>(sp =>
        {
            var log = sp.GetService<IPolicyDecisionLog>();
            var coordinator = sp.GetService<IScheduleCoordinator>();
            if (log is null || coordinator is null) return new NullPolicyDecisionLogQueue();
            return new PolicyDecisionLogQueue(
                log,
                coordinator,
                logger: sp.GetService<ILogger<PolicyDecisionLogQueue>>());
        });

        // The dispatcher itself. Singleton: handlers are stateless, the
        // resolver is singleton-shaped, the decision log queue is singleton-shaped.
        services.TryAddSingleton<PolicyActionDispatcher>();

        // Built-in per-action handlers. Each rendered as one IEnumerable<IPolicyActionHandler>
        // entry; the dispatcher's constructor builds the (action type -> handler) map.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPolicyActionHandler, AllowActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPolicyActionHandler, BlockActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPolicyActionHandler, ObserveActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPolicyActionHandler, TagActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPolicyActionHandler, ChallengeActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPolicyActionHandler, RateLimitActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPolicyActionHandler, ThrottleActionHandler>());

        return services;
    }
}
