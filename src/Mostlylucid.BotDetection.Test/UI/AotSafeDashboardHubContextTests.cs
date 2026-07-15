using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression coverage for the bug where <c>stylobot --enable-api</c> 500'd on EVERY request on
///     the AOT gateway. The SignalR broadcasters are pulled into the detection graph, and resolving
///     the strongly-typed <c>IHubContext&lt;StyloBotDashboardHub, IStyloBotDashboardHub&gt;</c> makes
///     SignalR build its typed-client proxy with <c>Reflection.Emit</c>
///     (<c>TypedClientBuilder.GenerateClientBuilder</c>). Native AOT disables dynamic code, so the
///     resolution threw <c>PlatformNotSupportedException</c> and took down the whole request pipeline.
///
///     <para>
///     <see cref="AotSafeDashboardHubContext"/> is the hand-written, Reflection.Emit-free replacement.
///     These tests prove it re-expresses each strongly-typed client call as
///     <c>IClientProxy.SendAsync(nameof(method), args)</c> with the exact wire method name and argument
///     order the JS client expects -- so swapping it in under AOT keeps the live-update contract intact.
///     A rename on <see cref="IStyloBotDashboardHub"/> breaks compilation (the proxy uses
///     <c>nameof</c>); a wrong argument order breaks these tests.
///     </para>
/// </summary>
public class AotSafeDashboardHubContextTests
{
    [Fact]
    public void All_BroadcastInvalidation_SendsCorrectWireCall()
    {
        var (ctx, fake) = Build();

        ctx.Clients.All.BroadcastInvalidation("summary");

        fake.Sends.Should().ContainSingle();
        fake.Sends[0].Method.Should().Be("BroadcastInvalidation");
        fake.Sends[0].Args.Should().Equal("summary");
    }

    [Fact]
    public void All_BroadcastAttackArc_SendsBothArgsInOrder()
    {
        var (ctx, fake) = Build();

        ctx.Clients.All.BroadcastAttackArc("GB", "High");

        fake.Sends[0].Method.Should().Be("BroadcastAttackArc");
        fake.Sends[0].Args.Should().Equal("GB", "High");
    }

    [Fact]
    public void Group_PolicyChanged_TargetsGroupAndSendsScopeKey()
    {
        var (ctx, fake) = Build();

        ctx.Clients.Group("policy:abc").PolicyChanged("domain:example.com");

        fake.LastGroup.Should().Be("policy:abc", "PolicyChanged is a group-scoped beacon");
        fake.Sends[0].Method.Should().Be("PolicyChanged");
        fake.Sends[0].Args.Should().Equal("domain:example.com");
    }

    [Fact]
    public void All_FingerprintDirty_SendsFingerprintAndSlot()
    {
        var (ctx, fake) = Build();

        ctx.Clients.All.FingerprintDirty("fp-123", "given");

        fake.Sends[0].Method.Should().Be("FingerprintDirty");
        fake.Sends[0].Args.Should().Equal("fp-123", "given");
    }

    [Fact]
    public void All_BroadcastDirty_SendsBeaconPayload()
    {
        var (ctx, fake) = Build();
        var beacon = new DashboardDirtyBeacon(42, new[] { "summary", "countries" });

        ctx.Clients.All.BroadcastDirty(beacon);

        fake.Sends[0].Method.Should().Be("BroadcastDirty");
        fake.Sends[0].Args.Should().ContainSingle().Which.Should().BeSameAs(beacon);
    }

    private static (IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> Ctx, RecordingClientProxy Fake) Build()
    {
        var fake = new RecordingClientProxy();
        var inner = new FakeUntypedHubContext(fake);
        return (new AotSafeDashboardHubContext(inner), fake);
    }

    // --- Fakes for the untyped hub the wrapper delegates to ------------------------------------

    private sealed class RecordingClientProxy : IClientProxy
    {
        public readonly List<(string Method, object?[] Args)> Sends = new();
        public string? LastGroup;

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Sends.Add((method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUntypedHubContext : IHubContext<StyloBotDashboardHub>
    {
        private readonly FakeUntypedClients _clients;
        public FakeUntypedHubContext(RecordingClientProxy proxy) => _clients = new FakeUntypedClients(proxy);
        public IHubClients Clients => _clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    // IHubClients : IHubClients<IClientProxy>. Every selector returns the same recording proxy;
    // Group(...) also records the requested group name so the group-scoped beacon can be asserted.
    private sealed class FakeUntypedClients : IHubClients
    {
        private readonly RecordingClientProxy _proxy;
        public FakeUntypedClients(RecordingClientProxy proxy) => _proxy = proxy;

        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName) { _proxy.LastGroup = groupName; return _proxy; }
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }
}
