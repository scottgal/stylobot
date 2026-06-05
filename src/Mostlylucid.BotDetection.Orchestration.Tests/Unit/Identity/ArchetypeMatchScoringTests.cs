using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Directly drives <see cref="IdentityArchetypeRegistry.FindNearest"/> with a known
///     Chrome-XHR raw-values dictionary and checks the archetype landed on. This is the
///     focused reproduction for the umbrella-centroid bug the BdfReplayTests theory
///     keeps catching: a Chrome browser doing an XHR request must NOT match googlebot
///     / mastodon, period. Running this test with score dump lets us see the per-
///     archetype masked-cosine values without the BDF rig in the loop.
/// </summary>
public sealed class ArchetypeMatchScoringTests
{
    private readonly ITestOutputHelper _output;
    private readonly IdentityVectorEncoder _encoder;
    private readonly IdentityArchetypeRegistry _registry;

    public ArchetypeMatchScoringTests(ITestOutputHelper output)
    {
        _output = output;
        var layout = IdentityVectorLayout.DefaultV1();
        _encoder = new IdentityVectorEncoder(layout);
        _registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, _encoder);
    }

    [Fact]
    public void ComposeRawValues_WritesUaFamily_FromHttpContextHeaders_LikeTheBdfRigDoes()
    {
        // The BDF replay rig builds a DefaultHttpContext, sets Request.Headers from
        // the BDF JSON, then runs the orchestrator. This test simulates EXACTLY that
        // path through ComposeRawValues so we can isolate "does the User-Agent header
        // turn into a hdr.ua_family raw value" from any of the surrounding orchestrator
        // / fingerprint-store / DI plumbing.
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/dashboard/api/topbots";
        ctx.Request.Headers["User-Agent"] =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";
        ctx.Request.Headers["Accept"] = "*/*";
        ctx.Request.Headers["Accept-Encoding"] = "gzip, br";
        ctx.Request.Headers["Sec-Fetch-Dest"] = "empty";
        ctx.Request.Headers["Sec-Fetch-Mode"] = "cors";
        ctx.Request.Headers["Sec-Fetch-Site"] = "same-origin";

        var state = new BlackboardState
        {
            HttpContext = ctx,
            Signals = new System.Collections.Concurrent.ConcurrentDictionary<string, object>(),
            CurrentRiskScore = 0,
            DetectionConfidence = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = new List<DetectionContribution>(),
            RequestId = "test",
            Elapsed = TimeSpan.Zero,
            SignalWriter = new System.Collections.Concurrent.ConcurrentDictionary<string, object>(),
        };

        var raw = IdentityVectorContributor.ComposeRawValues(state);

        _output.WriteLine("raw keys with non-null values:");
        foreach (var (k, v) in raw.Where(kv => kv.Value is not null).OrderBy(kv => kv.Key))
            _output.WriteLine($"  {k} = {v}");

        Assert.True(raw.ContainsKey("hdr.ua_family"),
            "ComposeRawValues did not write hdr.ua_family. BDF rig will produce vectors " +
            "without the UA family slot populated and the matcher cannot use it.");
        Assert.Equal("Chrome", raw["hdr.ua_family"]);
    }

    [Fact]
    public void EndToEnd_ChromeUbockXhrRequest_ComposeRawValues_Encode_FindNearest_LandsOnChromeArchetype()
    {
        // Exact replay of the BDF rig's last request from fp-99-chrome-ublock-xhr-misclass:
        // POST /dashboard/api/topbots with Chrome UA, XHR header shape, Accept: */*,
        // Accept-Encoding shortened by uBlock to "gzip, br", no Sec-Ch-Ua-* headers
        // (uBlock strips them), no Upgrade-Insecure-Requests (XHRs don't carry it).
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/dashboard/api/topbots";
        ctx.Request.Headers["User-Agent"] =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";
        ctx.Request.Headers["Accept"] = "*/*";
        ctx.Request.Headers["Accept-Language"] = "en-GB,en-US;q=0.9,en;q=0.8";
        ctx.Request.Headers["Accept-Encoding"] = "gzip, br";
        ctx.Request.Headers["Sec-Fetch-Dest"] = "empty";
        ctx.Request.Headers["Sec-Fetch-Mode"] = "cors";
        ctx.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        ctx.Request.Headers["Referer"] = "https://staging.stylobot.net/dashboard/";

        var state = new BlackboardState
        {
            HttpContext = ctx,
            Signals = new System.Collections.Concurrent.ConcurrentDictionary<string, object>(),
            CurrentRiskScore = 0,
            DetectionConfidence = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = new List<DetectionContribution>(),
            RequestId = "test",
            Elapsed = TimeSpan.Zero,
            SignalWriter = new System.Collections.Concurrent.ConcurrentDictionary<string, object>(),
        };

        var layout = IdentityVectorLayout.DefaultV1();
        var encoder = new IdentityVectorEncoder(layout);
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        var raw = IdentityVectorContributor.ComposeRawValues(state);
        var vector = encoder.Encode(raw);
        var match = registry.FindNearest(vector);

        Assert.NotNull(match);

        var scored = registry.All
            .Select(a => (a.ArchetypeId, Score: registry.ScoreAgainst(vector, a) ?? double.NegativeInfinity))
            .OrderByDescending(x => x.Score)
            .Take(8)
            .ToList();
        _output.WriteLine($"raw['hdr.ua_family'] = {raw.GetValueOrDefault("hdr.ua_family")}");
        foreach (var (id, score) in scored)
            _output.WriteLine($"  {id,-22} {score:F4}");
        _output.WriteLine($"winner: {match!.Archetype.ArchetypeId}");

        var winner = match.Archetype.ArchetypeId;
        Assert.False(
            winner.StartsWith("googlebot", StringComparison.OrdinalIgnoreCase)
                || winner.StartsWith("mastodon", StringComparison.OrdinalIgnoreCase),
            $"End-to-end: Chrome+uBlock XHR landed on '{winner}', expected chrome-*. " +
            "If THIS passes but the BDF integration test fails, the bug is in the " +
            "orchestrator wiring, not in encoder/registry. " +
            "Top scores: " + string.Join(", ", scored.Select(x => $"{x.ArchetypeId}={x.Score:F3}")));
    }

    [Fact]
    public void NullObservation_ProducesZeroScoreEverywhere_RatherThanInflatingGooglebot()
    {
        // The outer /bot-detection/bdf-replay request and any HttpClient ping with no
        // User-Agent and no Accept-* headers would, under the old masked-cosine, score
        // googlebot ~0.84 just because googlebot's centroid happens to overlap the
        // sparse-vector baseline (UIR=false, sec_fetch_pattern=0, sec_ch_ua_mobile=false).
        // That bound a "googlebot" fingerprint to every empty-headers test request, and
        // Pass 2's index search then pulled real Chrome XHR requests onto it. The
        // presence-aware similarity refuses to credit an archetype for a slot the
        // observation didn't populate, so null observations produce 0.0 across the
        // board -- no archetype wins, no poison fingerprint, no Pass 2 cross-binding.
        var layout = IdentityVectorLayout.DefaultV1();
        var encoder = new IdentityVectorEncoder(layout);
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        var raw = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var vector = encoder.Encode(raw);

        var match = registry.FindNearest(vector);
        Assert.NotNull(match);

        var scored = registry.All
            .Select(a => (a.ArchetypeId, Score: registry.ScoreAgainst(vector, a) ?? double.NegativeInfinity))
            .OrderByDescending(x => x.Score)
            .Take(8)
            .ToList();
        foreach (var (id, score) in scored)
            _output.WriteLine($"{id,-22} {score:F4}");

        Assert.True(scored.All(x => x.Score < 0.001),
            "Empty/null observation must score zero against every archetype. " +
            "Top scores: " + string.Join(", ", scored.Select(x => $"{x.ArchetypeId}={x.Score:F3}")));
    }

    [Fact]
    public void Chrome_XHR_Observation_DoesNotMatchGooglebot_OrMastodon()
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var encoder = new IdentityVectorEncoder(layout);
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        // Synthesise the raw values a Chrome browser would emit on a SignalR /negotiate
        // POST or /api/* fetch -- the request shape the live "Mastodon Family"
        // misclassification fired on (sig Cd3rCikN on staging, 2026-06-03).
        var raw = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["hdr.ua_family"] = "Chrome",
            ["hdr.accept"] = "*/*",
            ["hdr.accept_encoding_ordered"] = "gzip, br",
            ["hdr.sec_fetch_pattern"] = 7,
            ["hdr.upgrade_insecure_requests"] = false,
            ["session.method_pattern"] = "POST",
        };
        var vector = encoder.Encode(raw);

        var match = registry.FindNearest(vector);
        Assert.NotNull(match);

        // Dump the top-5 archetypes by score so the regression is debuggable when it
        // fails. The All collection is small (a dozen entries); a per-archetype scan
        // costs nothing in a unit test.
        var scored = registry.All
            .Select(a => (a.ArchetypeId, Score: registry.ScoreAgainst(vector, a) ?? double.NegativeInfinity))
            .OrderByDescending(x => x.Score)
            .Take(8)
            .ToList();
        foreach (var (id, score) in scored)
            _output.WriteLine($"{id,-22} {score:F4}");

        var winner = match!.Archetype.ArchetypeId;
        Assert.False(
            winner.StartsWith("googlebot", StringComparison.OrdinalIgnoreCase)
                || winner.StartsWith("mastodon", StringComparison.OrdinalIgnoreCase)
                || winner.StartsWith("python", StringComparison.OrdinalIgnoreCase)
                || winner.StartsWith("curl",   StringComparison.OrdinalIgnoreCase),
            $"Chrome-XHR observation landed on '{winner}', expected a chrome-* archetype " +
            "(chrome-desktop / chrome-xhr / chrome-privacy / mobile-chrome / headless-chrome). " +
            "Top scores: " + string.Join(", ", scored.Select(x => $"{x.ArchetypeId}={x.Score:F3}")));
    }

    // --------------------------------------------------------------------
    // Mahalanobis-style scoring contract tests (Task 2 of the
    // BDF umbrella-centroid fix plan). These tests will FAIL until Task 3
    // rewrites MaskedSimilarity to consume VarianceVector. The contract:
    //   - Tight archetypes (small variance) penalize deviations more strongly
    //   - Broad archetypes (large variance) tolerate larger deviations
    //   - Sparse observations near a tight centroid must NOT lose to an umbrella
    //   - Default-from-confidence variance fallback when VarianceVector is null
    //   - Numerical hygiene (epsilon floor) for zero-variance slots
    //   - Unpopulated observation dims remain skipped, not penalized
    // Slot offsets below are chosen to align with width-1 slots in
    // IdentityVectorLayout.DefaultV1 (network.is_datacenter=10, is_vpn=11,
    // is_tor=12, sec_ch_ua_mobile=31, hdr.dnt=57) so that mask[offset] reads
    // through MaskedSimilarity's slot iteration correctly.
    // --------------------------------------------------------------------

    [Fact]
    public void TightArchetype_WithSmallVariance_BeatsUmbrellaArchetype_WithLargeVariance_ForSparseObservation()
    {
        var dim = _encoder.Layout.Dimension;
        var centroid = new float[dim];
        centroid[10] = 1.0f;
        var mask = new float[dim];
        mask[10] = 0.9f;

        var tightVariance = new float[dim];
        Array.Fill(tightVariance, 0.001f);

        var broadVariance = new float[dim];
        Array.Fill(broadVariance, 0.1f);

        var tight = BuildArchetype("tight", centroid, mask, tightVariance);
        var broad = BuildArchetype("broad", centroid, mask, broadVariance);

        var observation = new float[dim];
        observation[10] = 0.95f;

        var tightScore = ScoreAgainst(observation, tight);
        var broadScore = ScoreAgainst(observation, broad);

        Assert.True(tightScore > broadScore,
            $"small variance must penalize less for a near-centroid observation than large variance " +
            $"(tight={tightScore:F4}, broad={broadScore:F4})");
    }

    [Fact]
    public void BroadArchetype_WithLargeVariance_BeatsTightArchetype_ForDistantObservation()
    {
        var dim = _encoder.Layout.Dimension;
        var centroid = new float[dim];
        centroid[10] = 1.0f;
        var mask = new float[dim];
        mask[10] = 0.9f;

        var tightVariance = new float[dim];
        Array.Fill(tightVariance, 0.001f);
        var broadVariance = new float[dim];
        Array.Fill(broadVariance, 0.1f);

        var tight = BuildArchetype("tight", centroid, mask, tightVariance);
        var broad = BuildArchetype("broad", centroid, mask, broadVariance);

        var observation = new float[dim];
        observation[10] = 0.5f;

        var tightScore = ScoreAgainst(observation, tight);
        var broadScore = ScoreAgainst(observation, broad);

        Assert.True(broadScore > tightScore,
            $"large variance must tolerate large deviations better than small variance " +
            $"(tight={tightScore:F4}, broad={broadScore:F4})");
    }

    [Fact]
    public void ArchetypeWithoutVariance_FallsBackToDefaultFromConfidence_AndStillScoresCorrectly()
    {
        var dim = _encoder.Layout.Dimension;

        var tightCentroid = new float[dim];
        tightCentroid[10] = 1.0f;
        tightCentroid[11] = 1.0f;
        var tightMask = new float[dim];
        tightMask[10] = 0.95f;
        tightMask[11] = 0.9f;

        var broadCentroid = new float[dim];
        broadCentroid[10] = 0.5f;
        var broadMask = new float[dim];
        broadMask[10] = 0.6f;

        var tight = BuildArchetype("tight", tightCentroid, tightMask, variance: null);
        var broad = BuildArchetype("broad", broadCentroid, broadMask, variance: null);

        var observation = new float[dim];
        observation[10] = 1.0f;
        observation[11] = 1.0f;

        var tightScore = ScoreAgainst(observation, tight);
        var broadScore = ScoreAgainst(observation, broad);

        Assert.True(tightScore > broadScore,
            "with no learned variance, the default-from-confidence rule must still favor " +
            $"tight high-confidence archetypes (tight={tightScore:F4}, broad={broadScore:F4})");
    }

    [Fact]
    public void ChromeXhrLikeObservation_LandsOnTightArchetype_NotOnUmbrella()
    {
        var dim = _encoder.Layout.Dimension;

        var chromeCentroid = new float[dim];
        chromeCentroid[10] = 1.0f;
        chromeCentroid[11] = 1.0f;
        chromeCentroid[12] = 1.0f;
        var chromeMask = new float[dim];
        chromeMask[10] = 0.9f;
        chromeMask[11] = 0.9f;
        chromeMask[12] = 0.95f;
        var chromeVariance = new float[dim];
        Array.Fill(chromeVariance, 0.05f);
        chromeVariance[10] = 0.005f;
        chromeVariance[11] = 0.005f;
        chromeVariance[12] = 0.005f;

        var umbrellaCentroid = new float[dim];
        umbrellaCentroid[10] = 0.0f;
        var umbrellaMask = new float[dim];
        umbrellaMask[10] = 0.6f;
        var umbrellaVariance = new float[dim];
        Array.Fill(umbrellaVariance, 0.2f);

        var chrome = BuildArchetype("chrome-tight", chromeCentroid, chromeMask, chromeVariance);
        var umbrella = BuildArchetype("umbrella", umbrellaCentroid, umbrellaMask, umbrellaVariance);

        var observation = new float[dim];
        observation[10] = 0.9f;
        // slots 11 and 12 left at 0 (XHR didn't populate them)

        var chromeScore = ScoreAgainst(observation, chrome);
        var umbrellaScore = ScoreAgainst(observation, umbrella);

        Assert.True(chromeScore > umbrellaScore,
            "sparse observations near a tight archetype's centroid must NOT lose to broad umbrella claims " +
            $"(chrome={chromeScore:F4}, umbrella={umbrellaScore:F4})");
    }

    [Fact]
    public void Mahalanobis_HandlesZeroVariance_WithEpsilonFloor()
    {
        var dim = _encoder.Layout.Dimension;
        var centroid = new float[dim];
        centroid[10] = 1.0f;
        var mask = new float[dim];
        mask[10] = 0.9f;
        var zeroVariance = new float[dim];
        var archetype = BuildArchetype("zero-variance", centroid, mask, zeroVariance);

        var observation = new float[dim];
        observation[10] = 0.5f;

        // Must not throw -- if epsilon floor isn't applied, division by zero will produce
        // NaN/Infinity rather than throw, but the score should still be finite.
        var score = ScoreAgainst(observation, archetype);

        Assert.True(!double.IsNaN(score) && !double.IsInfinity(score),
            $"epsilon floor must keep the score finite when variance is zero (got {score})");
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void Mahalanobis_AgainstUnpopulatedObservationDims_DoesNotPenalize()
    {
        var dim = _encoder.Layout.Dimension;
        var centroid = new float[dim];
        centroid[10] = 1.0f;
        centroid[57] = 1.0f;
        var mask = new float[dim];
        mask[10] = 0.9f;
        mask[57] = 0.9f;
        var variance = new float[dim];
        Array.Fill(variance, 0.05f);
        var archetype = BuildArchetype("multi-dim", centroid, mask, variance);

        var fullyPopulated = new float[dim];
        fullyPopulated[10] = 1.0f;
        fullyPopulated[57] = 1.0f;

        var partiallyPopulated = new float[dim];
        partiallyPopulated[10] = 1.0f;
        // slot at offset 57 left at 0

        var fullScore = ScoreAgainst(fullyPopulated, archetype);
        var partialScore = ScoreAgainst(partiallyPopulated, archetype);

        Assert.True(partialScore > 0.0, $"partial observation must not score zero (got {partialScore})");
        Assert.Equal(fullScore, partialScore, precision: 6);
    }

    // --------------------------------------------------------------------
    // Test helpers. BuildArchetype mirrors the YAML compile path's object
    // initializer but lets the test set centroid / mask / variance directly,
    // bypassing the encoder. ScoreAgainst delegates to the registry's public
    // ScoreAgainst (which the existing tests also drive) so the new tests pin
    // the SAME public surface the BDF rig and dashboard read through.
    // --------------------------------------------------------------------

    private static IdentityArchetype BuildArchetype(string id, float[] centroid, float[] mask, float[]? variance)
        => new()
        {
            ArchetypeId = id,
            Name = id,
            Description = "test",
            ArchetypeKind = "test",
            Centroid = centroid,
            DimensionMask = mask,
            VarianceVector = variance
        };

    private double ScoreAgainst(float[] observation, IdentityArchetype archetype)
        => _registry.ScoreAgainst(observation, archetype)
           ?? throw new InvalidOperationException(
               $"ScoreAgainst returned null for archetype '{archetype.ArchetypeId}'");
}