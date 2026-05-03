using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Contributors;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.ApiHolodeck.Services;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class BeaconContributorTests : IDisposable
{
    private readonly BeaconStore _store;
    private readonly BeaconContributor _contributor;
    private readonly string _dbPath;

    public BeaconContributorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"beacon-contrib-{Guid.NewGuid():N}.db");
        _store = new BeaconStore($"Data Source={_dbPath}");
        var options = Options.Create(new HolodeckOptions { BeaconCanaryLength = 8 });
        _contributor = new BeaconContributor(
            NullLogger<BeaconContributor>.Instance, _store, options);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task QueryParam_MatchesBeacon()
    {
        await _store.StoreAsync("abc12345", "old-fingerprint", "/wp-login.php", null, TimeSpan.FromHours(1));

        var context = new DefaultHttpContext();
        context.Request.Path = "/wp-admin/post.php";
        context.Request.QueryString = new QueryString("?nonce=abc12345&action=edit");

        var state = CreateState(context);
        var contributions = await _contributor.ContributeAsync(state);

        Assert.Single(contributions);
        Assert.True(state.Signals.ContainsKey("beacon.matched"));
        Assert.Equal("old-fingerprint", state.Signals["beacon.original_fingerprint"]);
    }

    [Fact]
    public async Task NoCanaryInRequest_NoMatch()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/products/123";
        context.Request.QueryString = new QueryString("?page=2");

        var state = CreateState(context);
        await _contributor.ContributeAsync(state);

        Assert.False(state.Signals.ContainsKey("beacon.matched"));
    }

    [Fact]
    public async Task CookieValue_MatchesBeacon()
    {
        await _store.StoreAsync("cook1234", "cookie-fp", "/.env", null, TimeSpan.FromHours(1));

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/data";
        context.Request.Headers["Cookie"] = "session=cook1234; other=value";

        var state = CreateState(context);
        await _contributor.ContributeAsync(state);

        Assert.True(state.Signals.ContainsKey("beacon.matched"));
        Assert.Equal("cookie-fp", state.Signals["beacon.original_fingerprint"]);
    }

    [Fact]
    public void Properties_CorrectNameAndPriority()
    {
        Assert.Equal("Beacon", _contributor.Name);
        Assert.Equal(2, _contributor.Priority);
        Assert.Empty(_contributor.TriggerConditions);
    }

    private static BlackboardState CreateState(HttpContext context)
    {
        var signals = new ConcurrentDictionary<string, object>();
        return new BlackboardState
        {
            HttpContext = context,
            Signals = signals,
            SignalWriter = signals,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = "test",
            Elapsed = TimeSpan.Zero
        };
    }
}
