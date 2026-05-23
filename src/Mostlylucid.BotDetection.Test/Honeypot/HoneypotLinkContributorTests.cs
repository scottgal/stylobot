using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Honeypot;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Honeypot;

public class HoneypotLinkContributorTests
{
    private static HoneypotLinkContributor Create(HoneypotDetectionOptions? options = null)
    {
        options ??= new HoneypotDetectionOptions();
        var monitor = new TestOptionsMonitor<HoneypotDetectionOptions>(options);
        var exemptStore = new ConfigHoneypotExemptStore(monitor);
        return new HoneypotLinkContributor(NullLogger<HoneypotLinkContributor>.Instance, monitor, exemptStore);
    }

    private static BlackboardState StateForPath(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        return BuildState(ctx);
    }

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
    public async Task Tier1Path_AwsCredentials_EmitsVerifiedBot()
    {
        var contributor = Create();
        var state = StateForPath("/.aws/credentials");

        var contributions = await contributor.ContributeAsync(state);

        var c = Assert.Single(contributions);
        Assert.Equal(0.95, c.ConfidenceDelta);
        Assert.Equal(2.0, c.Weight);
        Assert.True(c.Signals.ContainsKey(HoneypotLinkContributor.SignalTier1Hit));
        Assert.Equal("/.aws/credentials", c.Signals[HoneypotLinkContributor.SignalNormalizedPath]);
        // Tier 1 must publish a threat score so dashboard shows "Hostile" not "Benign".
        Assert.Equal(HoneypotLinkContributor.Tier1ThreatScore,
            (double)c.Signals[Mostlylucid.BotDetection.Models.SignalKeys.IntentThreatScore]);
    }

    [Fact]
    public async Task Tier2Path_WpLogin_EmitsSoftThreatScore()
    {
        var contributor = Create();
        var state = StateForPath("/wp-login.php");

        var contributions = await contributor.ContributeAsync(state);

        var c = Assert.Single(contributions);
        Assert.Equal(HoneypotLinkContributor.Tier2ThreatScore,
            (double)c.Signals[Mostlylucid.BotDetection.Models.SignalKeys.IntentThreatScore]);
    }

    [Fact]
    public async Task Tier1Path_EnvLocalSave_EmitsVerifiedBot()
    {
        var contributor = Create();
        var state = StateForPath("/.env.local.save");

        var contributions = await contributor.ContributeAsync(state);

        var c = Assert.Single(contributions);
        Assert.True(c.Signals.ContainsKey(HoneypotLinkContributor.SignalTier1Hit));
        Assert.Equal("/.env*", c.Signals[HoneypotLinkContributor.SignalMatchedPattern]);
    }

    [Fact]
    public async Task Tier2Path_WpLogin_EmitsSoftSignal()
    {
        var contributor = Create();
        var state = StateForPath("/wp-login.php");

        var contributions = await contributor.ContributeAsync(state);

        var c = Assert.Single(contributions);
        Assert.Equal(0.75, c.ConfidenceDelta);
        Assert.Equal(1.5, c.Weight);
        Assert.True(c.Signals.ContainsKey(HoneypotLinkContributor.SignalTier2Hit));
    }

    [Fact]
    public async Task Tier2Path_Exempt_EmitsNothing()
    {
        var contributor = Create(new HoneypotDetectionOptions
        {
            ExemptPaths = ["/wp-login.php"]
        });
        var state = StateForPath("/wp-login.php");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Empty(contributions);
    }

    [Fact]
    public async Task Tier1Path_ExemptIgnored_StillFires()
    {
        var contributor = Create(new HoneypotDetectionOptions
        {
            ExemptPaths = ["/.aws/credentials"]
        });
        var state = StateForPath("/.aws/credentials");

        var contributions = await contributor.ContributeAsync(state);

        var c = Assert.Single(contributions);
        Assert.True(c.Signals.ContainsKey(HoneypotLinkContributor.SignalTier1Hit));
    }

    [Fact]
    public async Task Disabled_EmitsNothing()
    {
        var contributor = Create(new HoneypotDetectionOptions { Enabled = false });
        var state = StateForPath("/.aws/credentials");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Empty(contributions);
    }

    [Fact]
    public async Task NormalPath_EmitsNothing()
    {
        var contributor = Create();
        var state = StateForPath("/products/123");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Empty(contributions);
    }

    [Fact]
    public async Task TaggerPrefilledItems_FastPath_UsedDirectly()
    {
        var contributor = Create();
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/products/123";
        ctx.Items[HoneypotPathTagger.ItemKeyTier] = HoneypotTier.Always;
        ctx.Items[HoneypotPathTagger.ItemKeyMatchedPattern] = "/dynamic-pattern";
        ctx.Items[HoneypotPathTagger.ItemKeyNormalizedPath] = "/dynamic-pattern";
        var state = BuildState(ctx);

        var contributions = await contributor.ContributeAsync(state);

        var c = Assert.Single(contributions);
        Assert.True(c.Signals.ContainsKey(HoneypotLinkContributor.SignalTier1Hit));
        Assert.Equal("/dynamic-pattern", c.Signals[HoneypotLinkContributor.SignalNormalizedPath]);
    }

    [Fact]
    public async Task AdditionalPaths_Operator_EmitsTier2()
    {
        var contributor = Create(new HoneypotDetectionOptions
        {
            AdditionalPaths = ["/api/v2/internal-debug"]
        });
        var state = StateForPath("/api/v2/internal-debug");

        var contributions = await contributor.ContributeAsync(state);

        var c = Assert.Single(contributions);
        Assert.True(c.Signals.ContainsKey(HoneypotLinkContributor.SignalTier2Hit));
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
