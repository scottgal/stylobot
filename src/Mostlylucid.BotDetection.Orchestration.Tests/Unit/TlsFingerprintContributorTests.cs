using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
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

    private TlsFingerprintContributor CreateContributor()
        => new(_loggerMock.Object, _configProviderMock.Object);

    private static BlackboardState CreateState(Dictionary<string, string>? headers = null, bool isHttps = true)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = isHttps ? "https" : "http";
        if (headers != null)
            foreach (var (key, value) in headers)
                httpContext.Request.Headers[key] = value;

        var signalDict = new ConcurrentDictionary<string, object>();
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
