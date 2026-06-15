using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Forward-DNS confirmation for fediverse +URL claims, per the 2026-06-15
///     claim-verify-trust gap analysis (Gap #3).
///
///     <para>
///     The pre-existing <see cref="FediverseDomainContributor"/> verified that a
///     claimed instance domain (e.g. <c>mastodon.social</c>) hosts ActivityPub
///     software via NodeInfo, but never compared the client IP against the
///     instance's A/AAAA records. An attacker could put <c>+https://mastodon.social/</c>
///     in the UA from any IP and <c>FriendlyDomainVerified=true</c> would still
///     fire. These tests pin the IP-side bind: the resolver returns a fake A
///     record, the contributor compares against the request's client IP, and
///     <c>verifiedbot.forward_dns_matched</c> reflects the outcome with
///     <c>verifiedbot.method=forward_dns</c> on a positive match.
///     </para>
/// </summary>
public class FediverseDomainContributorForwardDnsTests
{
    private sealed class FakeDnsResolver : IDnsResolver
    {
        private readonly Dictionary<string, IReadOnlyList<IPAddress>> _records = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string host, params IPAddress[] addresses)
            => _records[host] = addresses;

        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default)
        {
            return _records.TryGetValue(host, out var list)
                ? ValueTask.FromResult(list)
                : ValueTask.FromResult<IReadOnlyList<IPAddress>>(Array.Empty<IPAddress>());
        }
    }

    private sealed class CountingDnsResolver : IDnsResolver
    {
        private readonly Dictionary<string, IReadOnlyList<IPAddress>> _records = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ResolveCount { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string host, params IPAddress[] addresses)
            => _records[host] = addresses;

        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default)
        {
            ResolveCount[host] = ResolveCount.TryGetValue(host, out var c) ? c + 1 : 1;
            return _records.TryGetValue(host, out var list)
                ? ValueTask.FromResult(list)
                : ValueTask.FromResult<IReadOnlyList<IPAddress>>(Array.Empty<IPAddress>());
        }
    }

    private sealed class StubVerifier : IFediverseDomainVerifier
    {
        public bool? Result { get; init; } = true;

        public Task<bool?> VerifyAsync(string domain, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }

    private static BlackboardState BuildState(string ua, string clientIp)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = ua;
        if (IPAddress.TryParse(clientIp, out var ip))
            ctx.Connection.RemoteIpAddress = ip;
        var signals = new ConcurrentDictionary<string, object>();
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signals,
            SignalWriter = signals,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = "test",
            Elapsed = TimeSpan.Zero
        };
    }

    // Each test uses a unique instance domain to avoid the static
    // BoundedCache in FediverseDomainContributor cross-polluting results
    // between tests (the cache key is the instance domain).
    private static FediverseDomainContributor Create(IDnsResolver resolver, IFediverseDomainVerifier? verifier = null)
        => new FediverseDomainContributor(
            verifier ?? new StubVerifier(),
            resolver,
            NullLogger<FediverseDomainContributor>.Instance);

    [Fact]
    public async Task ForwardDns_Matches_Sets_VerifiedBotMethod_ForwardDns()
    {
        // Distinct domain per test to skip the cross-test cache.
        var resolver = new FakeDnsResolver();
        resolver.Add("match.example.test", IPAddress.Parse("203.0.113.42"));

        var state = BuildState(
            ua: "http.rb (Mastodon/4.x; +https://match.example.test/)",
            clientIp: "203.0.113.42");

        var contributor = Create(resolver);
        await contributor.ContributeAsync(state);

        Assert.True(state.GetSignal<bool>(SignalKeys.VerifiedBotForwardDnsMatched));
        Assert.Equal("forward_dns", state.GetSignal<string>(SignalKeys.VerifiedBotMethod));
    }

    [Fact]
    public async Task ForwardDns_Mismatch_Sets_Matched_False_No_Method()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("mismatch.example.test", IPAddress.Parse("203.0.113.42"));

        var state = BuildState(
            ua: "http.rb (Mastodon/4.x; +https://mismatch.example.test/)",
            clientIp: "198.51.100.99");  // not in the resolved set

        var contributor = Create(resolver);
        await contributor.ContributeAsync(state);

        Assert.True(state.HasSignal(SignalKeys.VerifiedBotForwardDnsMatched));
        Assert.False(state.GetSignal<bool>(SignalKeys.VerifiedBotForwardDnsMatched));
        // On mismatch the method must NOT be claimed -- absence of the signal
        // is the contract; if it were "forward_dns" the dashboard would render
        // a green check next to a spoofed claim.
        Assert.NotEqual("forward_dns", state.GetSignal<string>(SignalKeys.VerifiedBotMethod) ?? string.Empty);
    }

    [Fact]
    public async Task ForwardDns_NoDiscriminator_Skips_Without_Error()
    {
        var resolver = new FakeDnsResolver();
        var state = BuildState(ua: "curl/8.0", clientIp: "203.0.113.42");

        var contributor = Create(resolver);
        await contributor.ContributeAsync(state);

        // UA isn't fediverse-shaped so the contributor returns before the
        // forward-DNS step. No signal of any flavour should be written.
        Assert.False(state.HasSignal(SignalKeys.VerifiedBotForwardDnsMatched));
        Assert.False(state.HasSignal(SignalKeys.VerifiedBotForwardDnsError));
        Assert.False(state.HasSignal(SignalKeys.VerifiedBotMethod));
    }

    [Fact]
    public async Task ForwardDns_Cache_Hits_Avoid_SecondResolve()
    {
        // Distinct domain so cache is empty on first call. The second call
        // for the same domain must read from the 5-min in-process cache
        // rather than re-hitting the resolver.
        var resolver = new CountingDnsResolver();
        resolver.Add("cached.example.test", IPAddress.Parse("203.0.113.42"));

        var state1 = BuildState(
            ua: "http.rb (Mastodon/4.x; +https://cached.example.test/)",
            clientIp: "203.0.113.42");
        var state2 = BuildState(
            ua: "http.rb (Mastodon/4.x; +https://cached.example.test/)",
            clientIp: "203.0.113.42");

        var contributor = Create(resolver);
        await contributor.ContributeAsync(state1);
        await contributor.ContributeAsync(state2);

        Assert.Equal(1, resolver.ResolveCount["cached.example.test"]);
    }
}