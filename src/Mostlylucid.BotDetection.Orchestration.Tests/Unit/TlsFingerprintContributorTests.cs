using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.BotDetection.Definitions.TlsReference;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Unit tests for TlsFingerprintContributor's header-forwarded fingerprint reads.
///     The contributor relies on a reverse proxy (CF Transform Rule, nginx ssl_ja3,
///     Caddy ja3/ja4 plugin, HAProxy Lua) to compute the fingerprint and inject it as
///     a request header; these tests cover the read path for both JA3 and JA4.
/// </summary>
public class TlsFingerprintContributorTests
{
    private readonly Mock<ILogger<TlsFingerprintContributor>> _loggerMock = new();
    private readonly Mock<IDetectorConfigProvider> _configProviderMock = new();

    public TlsFingerprintContributorTests()
    {
        _configProviderMock.Setup(c => c.GetDefaults(It.IsAny<string>()))
            .Returns(new DetectorDefaults());
        _configProviderMock.Setup(c => c.GetManifest(It.IsAny<string>()))
            .Returns((DetectorManifest?)null);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string _, string _, int def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Returns((string _, string _, double def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, string _, bool def) => def);
    }

    private TlsFingerprintContributor CreateContributor(IJa3ReferenceIndex? index = null)
        => new(_loggerMock.Object, _configProviderMock.Object, index);

    private static BlackboardState CreateState(
        Dictionary<string, string>? headers = null,
        bool isHttps = true,
        string? userAgent = null,
        Dictionary<string, object>? signals = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = isHttps ? "https" : "http";
        if (userAgent != null) httpContext.Request.Headers.UserAgent = userAgent;
        if (headers != null)
            foreach (var (key, value) in headers)
                httpContext.Request.Headers[key] = value;

        var signalDict = new ConcurrentDictionary<string, object>();
        if (signals != null)
            foreach (var (k, v) in signals)
                signalDict[k] = v;

        return new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };
    }

    // === JA3 subset + version-delta check helpers ==========================
    // Real Chrome 120 desktop JA3 string from Castle.io 2024-01 reference.
    private const string Chrome120DesktopJa3String =
        "771,4865-4866-4867-49195-49199-49196-49200-52393-52392-49171-49172-156-157-47-53,0-23-65281-10-11-35-16-5-13-18-51-45-43-27-17513,29-23-24,0";

    private const string Chrome120DesktopJa3Hash = "cd08e31494f9531f560d64c695473da9";

    // damru-style: same TLS version, extensions, curves, formats; cipher list
    // is Chrome 120's minus two ciphers (49171, 49172). Strict subset, 2 missing.
    private const string DamruSubsetOfChrome120Ja3String =
        "771,4865-4866-4867-49195-49199-49196-49200-52393-52392-156-157-47-53,0-23-65281-10-11-35-16-5-13-18-51-45-43-27-17513,29-23-24,0";

    // Test-fixture MD5-shaped hashes: exactly 32 hex chars, recognisably
    // synthetic so they can't be confused with real corpus entries.
    private const string Chrome118DesktopJa3Hash = "1118abcdeadbeef0000000000c118c0c0";

    private static readonly Ja3Reference Chrome120Desktop = new()
    {
        Browser = "chrome",
        Version = 120,
        Mobile = false,
        Ja3Hash = Chrome120DesktopJa3Hash,
        Ja3String = Chrome120DesktopJa3String,
        Source = "test fixture"
    };

    private static readonly Ja3Reference Chrome118Desktop = new()
    {
        Browser = "chrome",
        Version = 118,
        Mobile = false,
        Ja3Hash = Chrome118DesktopJa3Hash,
        Ja3String = Chrome120DesktopJa3String, // same shape; standin
        Source = "test fixture"
    };

    private static IJa3ReferenceIndex BuildIndex(params Ja3Reference[] entries)
    {
        var mock = new Mock<IJa3ReferenceIndex>();
        mock.SetupGet(i => i.Count).Returns(entries.Length);
        mock.Setup(i => i.GetReference(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((string browser, int maxVersion, bool mobile) =>
                entries
                    .Where(e => e.Browser.Equals(browser, StringComparison.OrdinalIgnoreCase)
                                && e.Mobile == mobile
                                && e.Version <= maxVersion)
                    .OrderByDescending(e => e.Version)
                    .FirstOrDefault());
        mock.Setup(i => i.MatchAnyVersion(It.IsAny<string>()))
            .Returns((string hash) =>
                entries.FirstOrDefault(e => e.Ja3Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)));
        return mock.Object;
    }

    [Fact]
    public async Task SubsetOfRealChrome_DetectsDamruCipherBlacklisting()
    {
        // UA claims Chrome 145 desktop; the corpus' newest entry is Chrome 120
        // (intentional -- exercises the "at-or-below" lookup). Observed JA3
        // shares Chrome 120's version + extensions + curves + formats but the
        // cipher list is strict subset with 2 missing ciphers (49171/49172).
        var contributor = CreateContributor(BuildIndex(Chrome120Desktop));
        var state = CreateState(
            headers: new Dictionary<string, string>
            {
                ["X-JA3-String"] = DamruSubsetOfChrome120Ja3String
            },
            userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36");

        var contributions = await contributor.ContributeAsync(state);

        var subsetHit = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("subset", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(subsetHit);
        Assert.True(subsetHit.ConfidenceDelta > 0.6,
            $"Expected confidence > 0.6 for damru subset pattern, got {subsetHit.ConfidenceDelta}");
    }

    [Fact]
    public async Task ExactMatchToReference_NoSubsetSignal()
    {
        // Identical to reference -- not a subset, just a match.
        var contributor = CreateContributor(BuildIndex(Chrome120Desktop));
        var state = CreateState(
            headers: new Dictionary<string, string> { ["X-JA3-String"] = Chrome120DesktopJa3String },
            userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/145.0.0.0 Safari/537.36");

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("subset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VersionDelta_UaClaimsChrome145ButTlsMatchesChrome118_Flagged()
    {
        // Multilogin / Kameleo pattern: observed JA3 matches Chrome 118 (an
        // older release the cloak's underlying Chromium fork is locked to),
        // while the UA-CH spoof claims Chrome 145.
        var contributor = CreateContributor(BuildIndex(Chrome120Desktop, Chrome118Desktop));
        var state = CreateState(
            headers: new Dictionary<string, string> { ["X-JA3-Hash"] = Chrome118DesktopJa3Hash },
            userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/145.0.0.0 Safari/537.36");

        var contributions = await contributor.ContributeAsync(state);

        var deltaHit = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("version", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(deltaHit);
        Assert.True(deltaHit.ConfidenceDelta > 0.4,
            $"Expected confidence > 0.4 for version-delta pattern, got {deltaHit.ConfidenceDelta}");
    }

    [Fact]
    public async Task VersionDelta_UaMatchesObservedVersion_NoSignal()
    {
        // UA claims Chrome 120 and JA3 matches Chrome 120 -- exactly what we
        // expect from a real Chrome 120. No delta.
        var contributor = CreateContributor(BuildIndex(Chrome120Desktop));
        var state = CreateState(
            headers: new Dictionary<string, string> { ["X-JA3-Hash"] = Chrome120DesktopJa3Hash },
            userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0 Safari/537.36");

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("TLS matches", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DamruSubsetJa3_AgainstFirefoxUa_DoesNotFire()
    {
        // damru produces Chrome-shaped JA3s, not Firefox-shaped ones. A Chrome
        // subset JA3 paired with a Firefox UA should NOT trigger the subset
        // check -- the corpus has no Firefox entry that the observed JA3 could
        // possibly be a subset of, so the contributor must skip cleanly. Catches
        // regressions in ParseUaBrowserClaim that miscategorise Firefox as Chrome.
        var contributor = CreateContributor(BuildIndex(Chrome120Desktop));
        var firefoxUa = "Mozilla/5.0 (X11; Linux x86_64; rv:122.0) Gecko/20100101 Firefox/122.0";
        var state = CreateState(
            headers: new Dictionary<string, string> { ["X-JA3-String"] = DamruSubsetOfChrome120Ja3String },
            userAgent: firefoxUa);

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("subset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SubsetCheck_DifferentExtensionOrder_DoesNotFire()
    {
        // The subset rule requires EXACT match on extensions / curves / formats
        // and a strict subset only on the cipher list. A JA3 that differs in
        // the extension order isn't a damru-style cipher blacklist -- could be
        // a different browser or a Chromium fork with feature flag drift.
        var contributor = CreateContributor(BuildIndex(Chrome120Desktop));
        // Same cipher subset as the damru fixture, but extensions reordered
        // (45 and 27 swapped).
        var differentExtensions =
            "771,4865-4866-4867-49195-49199-49196-49200-52393-52392-156-157-47-53,0-23-65281-10-11-35-16-5-13-18-51-27-43-45-17513,29-23-24,0";
        var state = CreateState(
            headers: new Dictionary<string, string> { ["X-JA3-String"] = differentExtensions },
            userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/145.0.0.0 Safari/537.36");

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("subset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SubsetCheck_TooManyMissingCiphers_DoesNotFire()
    {
        // Subset rule caps at cipher_subset_max_missing (default 4). A
        // fingerprint with 8+ missing ciphers is too different to be a
        // deliberate blacklist -- more likely a different browser entirely.
        var contributor = CreateContributor(BuildIndex(Chrome120Desktop));
        // Only 4 ciphers remain -- 11 missing from Chrome 120's 15.
        var tooFew = "771,4865-4866-4867-49195,0-23-65281-10-11-35-16-5-13-18-51-45-43-27-17513,29-23-24,0";
        var state = CreateState(
            headers: new Dictionary<string, string> { ["X-JA3-String"] = tooFew },
            userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/145.0.0.0 Safari/537.36");

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("subset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoCorpusIndex_NeitherCheckFires()
    {
        // Index is null (corpus disabled / not loaded). The contributor must
        // not crash and must not emit subset / version-delta signals.
        var contributor = CreateContributor(index: null);
        var state = CreateState(
            headers: new Dictionary<string, string> { ["X-JA3-String"] = DamruSubsetOfChrome120Ja3String },
            userAgent: "Mozilla/5.0 Chrome/145.0.0.0");

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null
            && (c.Reason.Contains("subset", StringComparison.OrdinalIgnoreCase)
                || c.Reason.Contains("TLS matches", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ReadsJa4From_X_JA4_Header_AsTls_Ja4_AndTls_Ja4_Hash()
    {
        // CF Bot Management Enterprise / Caddy ja4 plugin / HAProxy Lua all canonically
        // forward JA4 as a single header. The contributor writes both tls.ja4 (for
        // IdentityVectorContributor) and tls.ja4_hash (for LearningTriggers).
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, string>
        {
            ["X-JA4"] = "t13d1516h2_8daaf6152771_b0da82dd1658"
        });

        await contributor.ContributeAsync(state);

        Assert.Equal("t13d1516h2_8daaf6152771_b0da82dd1658", state.Signals["tls.ja4"]);
        Assert.Equal("t13d1516h2_8daaf6152771_b0da82dd1658", state.Signals["tls.ja4_hash"]);
    }

    [Fact]
    public async Task ReadsJa4From_X_JA4_Fingerprint_HeaderAlias()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, string>
        {
            ["X-JA4-Fingerprint"] = "t13d1517h2_abcd1234ef56_deadbeef0000"
        });

        await contributor.ContributeAsync(state);

        Assert.Equal("t13d1517h2_abcd1234ef56_deadbeef0000", state.Signals["tls.ja4"]);
    }

    [Fact]
    public async Task ReadsJa4From_X_JA4_Hash_HeaderAlias()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, string>
        {
            ["X-JA4-Hash"] = "t13d1518h2_aaaabbbbcccc_ddddeeeeffff"
        });

        await contributor.ContributeAsync(state);

        Assert.Equal("t13d1518h2_aaaabbbbcccc_ddddeeeeffff", state.Signals["tls.ja4"]);
        Assert.Equal("t13d1518h2_aaaabbbbcccc_ddddeeeeffff", state.Signals["tls.ja4_hash"]);
    }

    [Fact]
    public async Task NoJa4Header_DoesNotWriteJa4Signals()
    {
        var contributor = CreateContributor();
        var state = CreateState();

        await contributor.ContributeAsync(state);

        Assert.False(state.Signals.ContainsKey("tls.ja4"));
        Assert.False(state.Signals.ContainsKey("tls.ja4_hash"));
    }

    [Fact]
    public async Task Ja3AndJa4_BothPresent_BothWritten()
    {
        // Real-world: CF Enterprise Transform Rule maps both ja3_hash and ja4 into
        // X-JA3-Hash and X-JA4; both must populate independently.
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, string>
        {
            ["X-JA3-Hash"] = "769,4866-4867,0-23-65281-10-11,29-23-24,0",
            ["X-JA4"] = "t13d1516h2_8daaf6152771_b0da82dd1658"
        });

        await contributor.ContributeAsync(state);

        Assert.Equal("769,4866-4867,0-23-65281-10-11,29-23-24,0", state.Signals["tls.ja3_hash"]);
        Assert.Equal("t13d1516h2_8daaf6152771_b0da82dd1658", state.Signals["tls.ja4"]);
    }
}
