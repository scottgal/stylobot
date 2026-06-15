using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Locks in the fix for Gap #1 of the 2026-06-15 claim-verify-trust gap
///     analysis (`docs/superpowers/specs/2026-06-15-claim-verify-trust-gap-analysis.md`).
///
///     The honest-bot rDNS-suffix path (UA carries +URL, rDNS of client IP
///     must end with the UA-claimed domain) used to live ONLY in
///     <see cref="Orchestration.ContributingDetectors.VerifiedBotContributor.CheckHonestBot"/>.
///     That contributor is excluded from every <see cref="Policies.DetectionPolicy"/>
///     (rDNS is too slow inline) AND its manifest carried
///     <c>skip_when: detection.early_exit</c>. Net result: rDNS-after-the-fact
///     for fediverse-shaped UAs (Mastodon / Pleroma / Akkoma) NEVER fired in
///     steady state. Only <c>verifiedbot.method=ip_range</c> rows ever appeared
///     in production; <c>verifiedbot.method=fcrdns</c> (forward-confirmed rDNS)
///     was dead code on the honest-bot side.
///
///     Fix (Option B per the spec): move the honest-bot rDNS-suffix logic into
///     <see cref="VerifiedBotRegistry.VerifyHonestBotAsync"/> and call it from
///     <see cref="BackgroundEnrichmentService"/>. The request path no longer
///     blocks on DNS; the gate doesn't matter any more (the contributor is
///     excluded by policy, and the manifest's misleading <c>skip_when</c> is
///     dropped to keep the YAML honest about what runs and where).
/// </summary>
public class VerifiedBotHonestBotTests
{
    private static VerifiedBotRegistry NewRegistry()
    {
        var httpFactory = new StubHttpClientFactory();
        var options = Options.Create(new VerifiedBotRegistryOptions());
        return new VerifiedBotRegistry(
            NullLogger<VerifiedBotRegistry>.Instance,
            httpFactory,
            options,
            new Mostlylucid.BotDetection.Test.Scheduling.Helpers.RecordingScheduleCoordinator());
    }

    [Fact]
    public async Task VerifyHonestBotAsync_returns_null_when_userAgent_has_no_url_claim()
    {
        // A plain Chrome UA has no +URL claim -- nothing for rDNS to verify
        // against. Must return null without any DNS work.
        var registry = NewRegistry();

        var result = await registry.VerifyHonestBotAsync(
            "Mozilla/5.0 (Macintosh) AppleWebKit/537.36 Chrome/120.0",
            "203.0.113.10");

        Assert.Null(result);
    }

    [Fact]
    public async Task VerifyHonestBotAsync_returns_null_when_inputs_empty()
    {
        var registry = NewRegistry();

        Assert.Null(await registry.VerifyHonestBotAsync(null, "203.0.113.10"));
        Assert.Null(await registry.VerifyHonestBotAsync("", "203.0.113.10"));
        Assert.Null(await registry.VerifyHonestBotAsync("Mastodon (+https://example.org)", null));
        Assert.Null(await registry.VerifyHonestBotAsync("Mastodon (+https://example.org)", ""));
    }

    [Fact]
    public async Task VerifyHonestBotAsync_RunsForFediverseClaim_EvenWhenEarlyExitWasSet()
    {
        // The whole point of Gap #1: this path must run regardless of any
        // detection.early_exit gate from FastPathReputation. The new home
        // (VerifiedBotRegistry + BackgroundEnrichmentService) doesn't read
        // the early_exit signal at all -- it's invoked off the hot path.
        //
        // We exercise the path with an in-process DNS resolver stub so the
        // test doesn't depend on real network DNS.
        var registry = NewRegistry();
        var resolverCalled = false;
        registry.RdnsResolverOverride = (ip, _) =>
        {
            resolverCalled = true;
            // Simulate the canonical honest-bot shape: client IP rDNSes to a
            // host on the same domain the UA claimed.
            return Task.FromResult<string?>("relay-7.mastodon.example.org");
        };

        var result = await registry.VerifyHonestBotAsync(
            "http.rb/5.2.0 (Mastodon/4.2.10; +https://mastodon.example.org/)",
            "198.51.100.20");

        Assert.True(resolverCalled, "rDNS resolver must run -- early_exit gate is no longer in scope here");
        Assert.NotNull(result);
        Assert.Equal("mastodon.example.org", result!.ClaimedDomain);
        Assert.Equal("relay-7.mastodon.example.org", result.ResolvedHostname);
        Assert.True(result.SuffixMatched, "rDNS hostname is a sub-domain of the UA-claimed domain");
        Assert.Equal("fcrdns", result.VerificationMethod);
    }

    [Fact]
    public async Task VerifyHonestBotAsync_reports_mismatch_when_rdns_resolves_elsewhere()
    {
        // CDNs / shared hosting can legitimately produce a different rDNS
        // domain than the UA-claimed one. We must still emit a result so
        // BackgroundEnrichmentService can route it through the reputation
        // updater (weaker signal than a clean match, but non-null).
        var registry = NewRegistry();
        registry.RdnsResolverOverride = (_, _) => Task.FromResult<string?>("ec2-1-2-3-4.compute.amazonaws.com");

        var result = await registry.VerifyHonestBotAsync(
            "Pleroma 2.5.0 (+https://pleroma.example.net/)",
            "192.0.2.30");

        Assert.NotNull(result);
        Assert.Equal("pleroma.example.net", result!.ClaimedDomain);
        Assert.False(result.SuffixMatched);
        Assert.Equal("fcrdns_mismatch", result.VerificationMethod);
    }

    [Fact]
    public async Task VerifyHonestBotAsync_returns_null_when_rdns_yields_nothing()
    {
        // No PTR record -- nothing to compare against. Must not synthesise
        // a "spoofed" verdict (rDNS absence is legitimately ambiguous).
        var registry = NewRegistry();
        registry.RdnsResolverOverride = (_, _) => Task.FromResult<string?>(null);

        var result = await registry.VerifyHonestBotAsync(
            "Akkoma 3.10.4 (+https://akkoma.example.com/)",
            "203.0.113.40");

        Assert.Null(result);
    }

    [Fact]
    public void VerifiedBotManifest_does_not_skip_when_detection_early_exit()
    {
        // The whole gate that hid rDNS-after-the-fact for two months. Lock
        // it out: if anyone reintroduces the skip_when, this test fails and
        // the gap re-opens.
        //
        // The manifest is shipped as an embedded resource on the
        // Mostlylucid.BotDetection assembly. Read it back via the resource
        // stream rather than the loader pipeline so we are insulated from
        // any future refactor of DetectorManifestLoader.
        var asm = typeof(VerifiedBotRegistry).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("verifiedbot.detector.yaml", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resourceName)
                          ?? throw new InvalidOperationException("verifiedbot.detector.yaml missing from assembly resources");
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();

        // The only thing that actually gates the detector is the YAML list
        // value under `skip_when:`. We must not parse the leading comments --
        // the comment block above the skip_when key deliberately mentions the
        // old gate ("the previous skip_when: detection.early_exit ...") so
        // future readers know what changed. What is forbidden is the LIST
        // ITEM "- detection.early_exit" appearing under the key.
        Assert.DoesNotContain("- detection.early_exit", yaml);

        // Belt-and-braces: locate the actual skip_when value and confirm it
        // is the empty list. Lines like
        //   skip_when: []
        // pass; lines like
        //   skip_when:
        //     - detection.early_exit
        // fail.
        var lines = yaml.Split('\n');
        var skipWhenIdx = Array.FindIndex(lines, l => l.TrimStart().StartsWith("skip_when:"));
        Assert.True(skipWhenIdx >= 0, "verifiedbot.detector.yaml must declare a skip_when key");
        var skipWhenLine = lines[skipWhenIdx].Trim();
        Assert.True(
            skipWhenLine == "skip_when: []" || skipWhenLine.EndsWith("skip_when: []", StringComparison.Ordinal),
            $"skip_when must be the empty list -- found '{skipWhenLine}'");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}