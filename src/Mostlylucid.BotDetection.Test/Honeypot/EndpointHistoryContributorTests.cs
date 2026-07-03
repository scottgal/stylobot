using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Honeypot;
using Mostlylucid.BotDetection.Lifecycle;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Honeypot;

public class EndpointHistoryContributorTests
{
    private static BlackboardState BuildState(HttpContext ctx)
    {
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

    [Fact]
    public async Task NoLifecycle_EmitsNothing()
    {
        var store = new StubStore(_ => null);
        var contributor = new EndpointHistoryContributor(
            NullLogger<EndpointHistoryContributor>.Instance, store);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/never-seen";

        var contributions = await contributor.ContributeAsync(BuildState(ctx));

        Assert.Empty(contributions);
    }

    [Fact]
    public async Task LifecycleWithNo2xxHistory_EmitsNothing()
    {
        var store = new StubStore(_ => new PathLifecycle
        {
            Path = "/.env",
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
            Total2xx = 0,
            Total4xx = 12,
            TotalOther = 0
        });
        var contributor = new EndpointHistoryContributor(
            NullLogger<EndpointHistoryContributor>.Instance, store);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/.env";

        var contributions = await contributor.ContributeAsync(BuildState(ctx));

        Assert.Empty(contributions); // not "formerly real" -- never 2xx'd
    }

    [Fact]
    public async Task FormerlyReal_EmitsThreatBoost()
    {
        var last2xx = DateTime.UtcNow.AddDays(-30);
        var first4xx = DateTime.UtcNow.AddDays(-5);
        var store = new StubStore(_ => new PathLifecycle
        {
            Path = "/old-api/users",
            FirstSeenUtc = last2xx,
            LastSeenUtc = DateTime.UtcNow,
            Total2xx = 1500,
            Total4xx = 47,
            Last2xxUtc = last2xx,
            First4xxAfter2xxUtc = first4xx
        });
        var contributor = new EndpointHistoryContributor(
            NullLogger<EndpointHistoryContributor>.Instance, store);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/old-api/users";

        var contributions = await contributor.ContributeAsync(BuildState(ctx));

        var c = Assert.Single(contributions);
        Assert.True(c.Signals.ContainsKey(EndpointHistoryContributor.SignalHistoryMatch));
        Assert.Equal(EndpointHistoryContributor.HistoryThreatBoost,
            (double)c.Signals[SignalKeys.IntentThreatScore]);
        Assert.Equal(1500, (int)c.Signals[EndpointHistoryContributor.SignalTotal2xx]);
        Assert.Contains("served 2xx", c.Reason);
    }

    private sealed class StubStore : IPathLifecycleStore
    {
        private readonly Func<string, PathLifecycle?> _lookup;
        public StubStore(Func<string, PathLifecycle?> lookup) => _lookup = lookup;
        public Task RecordResponseAsync(RequestScope scope, string path, int statusCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PathLifecycle?> GetAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(_lookup(path));
    }
}
