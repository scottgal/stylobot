using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.InternalPlumbing;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Xunit;

namespace Mostlylucid.BotDetection.Test.InternalPlumbing;

/// <summary>
///     Pins <see cref="InternalPlumbingCatalog"/>: the product's OWN plumbing paths
///     (the SignalR dashboard hub + the client-side fingerprint beacon) are matched at
///     segment boundaries so negotiate/invoke sub-paths inherit the prefix, but
///     look-alike paths (hubspot, hub-events) never match.
/// </summary>
public class InternalPlumbingCatalogTests
{
    private static InternalPlumbingCatalog CatalogWith(params string[] paths)
    {
        var options = new InternalPlumbingOptions { Paths = paths.ToList() };
        return new InternalPlumbingCatalog(Options.Create(options));
    }

    [Fact]
    public void Default_hub_path_matches()
    {
        var catalog = CatalogWith("/stylobot/hub");

        Assert.True(catalog.IsInternalPlumbingPath(new PathString("/stylobot/hub")));
    }

    [Theory]
    [InlineData("/stylobot/hub")]
    [InlineData("/stylobot/hub/negotiate")]
    [InlineData("/stylobot/hub/negotiate/connectionid")]
    public void Hub_prefix_matches_signalr_subpaths(string path)
    {
        // Query strings never appear in Request.Path (they live in Request.QueryString),
        // so no query-bearing inputs here — the real /stylobot/hub?negotiateVersion=1
        // reaches the atom as Path="/stylobot/hub".
        var catalog = CatalogWith("/stylobot/hub");

        Assert.True(catalog.IsInternalPlumbingPath(new PathString(path)),
            $"hub prefix must match {path}");
    }

    [Theory]
    [InlineData("/stylobot/hubspot")]
    [InlineData("/stylobot/hub-events")]
    [InlineData("/other/stylobot/hub")]
    [InlineData("/stylobot/hubx/negotiate")]
    [InlineData("/hubspot")]
    public void Lookalike_paths_never_match(string path)
    {
        var catalog = CatalogWith("/stylobot/hub");

        Assert.False(catalog.IsInternalPlumbingPath(new PathString(path)),
            $"lookalike {path} must not match the hub prefix");
    }

    [Fact]
    public void Beacon_path_matches_exact_and_not_prefix_cousins()
    {
        var catalog = CatalogWith("/bot-detection/fingerprint");

        Assert.True(catalog.IsInternalPlumbingPath(new PathString("/bot-detection/fingerprint")));
        Assert.False(catalog.IsInternalPlumbingPath(new PathString("/bot-detection/fingerprints")));
        Assert.False(catalog.IsInternalPlumbingPath(new PathString("/bot-detection/")));
    }

    [Fact]
    public void Case_insensitive_segment_boundary_matching()
    {
        var catalog = CatalogWith("/stylobot/hub");

        Assert.True(catalog.IsInternalPlumbingPath(new PathString("/StyloBot/Hub/negotiate")));
    }
}

/// <summary>
///     Pins the ledger classification: the <c>request.internal_plumbing</c> signal
///     (raised by <see cref="Mostlylucid.BotDetection.Orchestration.Atoms.InternalPlumbingAtom"/>)
///     promotes the request to <see cref="BotType.Internal"/> so the composer's
///     trusted-and-aligned clamp lands the per-request band on
///     <see cref="RiskBand.Low"/> — the dashboard's own hub/beacon traffic can never
///     read as a high-threat visitor, regardless of what the scoring atoms produce.
/// </summary>
public class InternalPlumbingClassificationTests
{
    private static readonly Dictionary<string, object> HubSignals = new()
    {
        [SignalKeys.InternalPlumbing] = true,
        [SignalKeys.UserAgent] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/126 Safari/537.36",
    };

    private static readonly Dictionary<string, object> HubSignalsHighProbability = new()
    {
        [SignalKeys.InternalPlumbing] = true,
        [SignalKeys.UserAgent] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/126 Safari/537.36",
    };

    private static readonly Dictionary<string, object> NoPlumbingSignals = new()
    {
        [SignalKeys.UserAgent] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/126 Safari/537.36",
    };

    [Fact]
    public void Hub_path_signal_classifies_Internal_and_clamps_to_Low()
    {
        var ledger = new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionLedger("test-internal-plumbing");
        ledger.AddContribution(new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution
        {
            DetectorName = "BrowserModeClassifier",
            Category = "Request",
            ConfidenceDelta = 1.0,
            Weight = 1.0,
            Reason = "SignalR hub connection shape",
        });

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: HubSignals);

        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
        Assert.Equal(RiskBand.Low, evidence.RiskBand);
    }

    [Fact]
    public void Hub_path_with_high_probability_still_Internal_and_Low()
    {
        var ledger = new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionLedger("test-internal-plumbing-high");
        ledger.AddContribution(new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution
        {
            DetectorName = "BrowserModeClassifier",
            Category = "Request",
            ConfidenceDelta = 1.0,
            Weight = 1.0,
            Reason = "SignalR hub connection shape",
        });

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: HubSignalsHighProbability);

        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
        Assert.Equal(RiskBand.Low, evidence.RiskBand);
    }

    [Fact]
    public void Without_plumbing_signal_a_high_scoring_request_is_not_Internal()
    {
        var ledger = new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionLedger("test-no-plumbing");
        ledger.AddContribution(new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 1.0,
            Weight = 1.0,
            Reason = "Hostile UA shape",
        });

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: NoPlumbingSignals);

        // The Low clamp is SPECIFIC to the plumbing/network-trust classification: a
        // regular high-probability request without the signal must not read as Low.
        Assert.NotEqual(BotType.Internal, evidence.PrimaryBotType);
        Assert.NotEqual(RiskBand.Low, evidence.RiskBand);
    }
}
