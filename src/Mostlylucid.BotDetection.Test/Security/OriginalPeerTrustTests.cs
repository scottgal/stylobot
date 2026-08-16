using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Security;

/// <summary>
///     Pins the original-peer capture (2026-08-16, the staging 429 incident): the
///     gateway's UseForwardedHeaders middleware (TrustAllForwardedProxies — the
///     staging/prod edge config) OVERWRITES <c>Connection.RemoteIpAddress</c> with the
///     X-Forwarded-For client IP BEFORE the detection runs. The product's own calls
///     legitimately carry the browser's forwarded headers, so their "peer" read as the
///     browser's public IP and the peer-only InternalTrust evaluation never fired — the
///     site's compose calls hit the rate-limit enforcer instead of classifying Internal.
///     The gateway host now stashes the ORIGINAL TCP peer before the forwarded-headers
///     processing; IpAtom prefers the stash and falls back to the (possibly overwritten)
///     Connection.RemoteIpAddress.
/// </summary>
public class OriginalPeerTrustTests
{
    private static IpAtom BuildAtom(HttpContext context) => new(
        NullLogger<IpAtom>.Instance,
        new StubDetectorConfigProvider(),
        new StaticHttpContextAccessor(context),
        botListDatabase: null,
        asnLookup: null,
        proxyEnvironment: null);

    [Fact]
    public async Task Original_peer_stash_wins_over_the_forwarded_overwrite()
    {
        var http = new DefaultHttpContext();
        // The forwarded-headers overwrite: Connection.RemoteIpAddress = the browser's
        // public IP (the X-Forwarded-For value the site's own calls carry).
        http.Connection.RemoteIpAddress = IPAddress.Parse("90.204.232.26");
        // The gateway's pre-forwarded capture: the real TCP peer = the site's docker IP.
        http.Items[BotDetectionMiddleware.OriginalTcpPeerItemKey] = IPAddress.Parse("172.18.0.6");

        var sink = new SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));
        await BuildAtom(http).DetectAsync(sink, "test");

        sink.ReadBoolHint(SignalKeys.IpIsTrustedInternal).Should().BeTrue(
            "the original TCP peer (RFC1918) must win over the forwarded public IP — " +
            "the peer-only trust invariant holds across the middleware ordering");
    }

    [Fact]
    public async Task Without_the_stash_the_overwritten_connection_peer_is_used()
    {
        // Hosts without the capture middleware (direct-embed, sidecar): the fallback is
        // the (possibly overwritten) Connection.RemoteIpAddress — the public peer is not
        // trusted, preserving the deny case.
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("90.204.232.26");

        var sink = new SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));
        await BuildAtom(http).DetectAsync(sink, "test");

        sink.ReadBoolHint(SignalKeys.IpIsTrustedInternal).Should().BeFalse(
            "a public peer is never trusted — the deny case must hold without the stash");
    }
}
