using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.BotDetection.UI.Hubs;

/// <summary>
///     AOT-safe replacement for the framework's strongly-typed
///     <see cref="IHubContext{THub,T}"/> where <c>T = <see cref="IStyloBotDashboardHub"/></c>.
///
///     <para>
///     SignalR builds the strongly-typed client proxy for <c>IHubContext&lt;Hub, TClient&gt;</c>
///     with <c>System.Reflection.Emit</c> (<c>TypedClientBuilder&lt;TClient&gt;.GenerateClientBuilder</c>).
///     Merely <em>resolving</em> the typed hub context from DI constructs that proxy, so on a runtime
///     with dynamic code disabled -- the <c>stylobot</c> Console gateway publishes Native AOT, which
///     sets <c>RuntimeFeature.IsDynamicCodeSupported == false</c> -- the resolution throws
///     <c>PlatformNotSupportedException</c>. That takes down <c>--enable-api</c>: the SignalR
///     broadcasters are pulled into the detection graph, so every request 500s and the dashboard
///     read surface never comes up.
///     </para>
///
///     <para>
///     This wrapper delegates to the <em>untyped</em> <see cref="IHubContext{THub}"/> (which needs no
///     Reflection.Emit) and re-expresses each strongly-typed client call as
///     <c>IClientProxy.SendAsync(nameof(method), args)</c>. The wire contract is identical -- the JS
///     client already subscribes by method-name string -- and <c>nameof(IStyloBotDashboardHub.X)</c>
///     keeps the method names compile-checked, so a rename on the interface still breaks the build.
///     It is registered only when dynamic code is unavailable (see
///     <see cref="AotSafeDashboardHubContextExtensions.AddAotSafeDashboardHubContext"/>); JIT hosts
///     keep the native typed hub unchanged.
///     </para>
/// </summary>
internal sealed class AotSafeDashboardHubContext : IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>
{
    private readonly IHubContext<StyloBotDashboardHub> _inner;

    public AotSafeDashboardHubContext(IHubContext<StyloBotDashboardHub> inner) => _inner = inner;

    public IHubClients<IStyloBotDashboardHub> Clients => new Clientset(_inner.Clients);

    public IGroupManager Groups => _inner.Groups;

    /// <summary>Maps every <see cref="IHubClients{T}"/> selector to the untyped selector, wrapping the resulting proxy.</summary>
    private sealed class Clientset : IHubClients<IStyloBotDashboardHub>
    {
        private readonly IHubClients _inner;
        public Clientset(IHubClients inner) => _inner = inner;

        public IStyloBotDashboardHub All => new Proxy(_inner.All);
        public IStyloBotDashboardHub AllExcept(IReadOnlyList<string> excludedConnectionIds) => new Proxy(_inner.AllExcept(excludedConnectionIds));
        public IStyloBotDashboardHub Client(string connectionId) => new Proxy(_inner.Client(connectionId));
        public IStyloBotDashboardHub Clients(IReadOnlyList<string> connectionIds) => new Proxy(_inner.Clients(connectionIds));
        public IStyloBotDashboardHub Group(string groupName) => new Proxy(_inner.Group(groupName));
        public IStyloBotDashboardHub GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new Proxy(_inner.GroupExcept(groupName, excludedConnectionIds));
        public IStyloBotDashboardHub Groups(IReadOnlyList<string> groupNames) => new Proxy(_inner.Groups(groupNames));
        public IStyloBotDashboardHub User(string userId) => new Proxy(_inner.User(userId));
        public IStyloBotDashboardHub Users(IReadOnlyList<string> userIds) => new Proxy(_inner.Users(userIds));
    }

    /// <summary>
    ///     Hand-written <see cref="IStyloBotDashboardHub"/> proxy over an <see cref="IClientProxy"/>.
    ///     This is exactly what <c>TypedClientBuilder</c> would emit at runtime, written by hand so no
    ///     dynamic code is needed. Keep in sync with <see cref="IStyloBotDashboardHub"/> -- the
    ///     compiler enforces the method set (an added interface method fails to compile here) and
    ///     <c>nameof</c> enforces the wire names.
    /// </summary>
    private sealed class Proxy : IStyloBotDashboardHub
    {
        private readonly IClientProxy _proxy;
        public Proxy(IClientProxy proxy) => _proxy = proxy;

        public Task BroadcastInvalidation(string signal)
            => _proxy.SendAsync(nameof(IStyloBotDashboardHub.BroadcastInvalidation), signal);

        public Task BroadcastAttackArc(string countryCode, string riskBand)
            => _proxy.SendAsync(nameof(IStyloBotDashboardHub.BroadcastAttackArc), countryCode, riskBand);

        public Task PolicyChanged(string scopeKey)
            => _proxy.SendAsync(nameof(IStyloBotDashboardHub.PolicyChanged), scopeKey);

        public Task FingerprintDirty(string fingerprintId, string slot)
            => _proxy.SendAsync(nameof(IStyloBotDashboardHub.FingerprintDirty), fingerprintId, slot);

        public Task BroadcastDirty(DashboardDirtyBeacon beacon)
            => _proxy.SendAsync(nameof(IStyloBotDashboardHub.BroadcastDirty), beacon);
    }
}

/// <summary>DI wiring for <see cref="AotSafeDashboardHubContext"/>.</summary>
public static class AotSafeDashboardHubContextExtensions
{
    /// <summary>
    ///     Overrides the strongly-typed <c>IHubContext&lt;StyloBotDashboardHub, IStyloBotDashboardHub&gt;</c>
    ///     with an AOT-safe wrapper when the runtime has dynamic code disabled (Native AOT). A no-op on
    ///     JIT hosts, which keep the framework's typed hub. Idempotent: call it after every
    ///     <c>AddSignalR()</c>; the last closed-generic registration wins over SignalR's open-generic one.
    /// </summary>
    public static IServiceCollection AddAotSafeDashboardHubContext(this IServiceCollection services)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            return services; // JIT: the native typed hub works; leave it untouched.

        services.AddSingleton<IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>>(sp =>
            new AotSafeDashboardHubContext(sp.GetRequiredService<IHubContext<StyloBotDashboardHub>>()));
        return services;
    }
}
