using FluentAssertions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity.BrowserModes;

/// <summary>
///     Coverage for the T13 YAML-backed mode-centroid seed source. The seed
///     source is the runtime path; <see cref="HardcodedBrowserModeSeedSource"/>
///     stays as the unit-test fake. These tests pin the load-bearing
///     contracts:
///     <list type="bullet">
///         <item><description>Every YAML in <c>Definitions/BrowserModes</c>
///         produces a <see cref="ModeSeed"/> the catalogue can persist (id
///         non-empty, vector layout-sized).</description></item>
///         <item><description>YAMLs with a <c>seed:</c> block produce a
///         non-zero centroid via <see cref="ModeVectorEncoder"/>; YAMLs
///         without (the <c>unknown</c> sentinel + <c>prefetch</c>) produce
///         all-zero centroids.</description></item>
///         <item><description>The encoded centroid for <c>navigation</c>
///         matches what the encoder produces directly from the canonical
///         signal values — pins the YAML → encoder pipeline against a
///         silent drift in either side.</description></item>
///     </list>
/// </summary>
public sealed class YamlBrowserModeSeedSourceTests
{
    [Fact]
    public void LoadYamlSeeds_reads_every_browser_mode_yaml()
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var source = new YamlBrowserModeSeedSource(layout);

        var seeds = source.LoadYamlSeeds();

        seeds.Should().NotBeEmpty();
        seeds.Should().AllSatisfy(seed =>
        {
            seed.ModeId.Should().NotBeNullOrWhiteSpace();
            seed.SeedCentroid.Length.Should().Be(layout.Dimension);
        });
    }

    [Fact]
    public void LoadYamlSeeds_includes_every_known_mode_id()
    {
        // Inventory pin: the YAML catalogue must surface every id the
        // hardcoded seed source ships, plus prefetch (which lives in YAML
        // but not in the test fake). A missing id here means a YAML file
        // didn't deserialise — the loader's catch swallows the exception
        // and the catalogue would silently miss the mode at boot.
        var layout = IdentityVectorLayout.DefaultV1();
        var source = new YamlBrowserModeSeedSource(layout);

        var seeds = source.LoadYamlSeeds();
        var ids = seeds.Select(s => s.ModeId).ToHashSet();

        ids.Should().Contain(new[]
        {
            "navigation",
            "xhr",
            "sub-resource",
            "signalr-negotiate",
            "websocket-upgrade",
            "bot-raw",
            "unknown",
            "prefetch",
        });
    }

    [Fact]
    public void LoadYamlSeeds_encodes_navigation_via_mode_vector_encoder()
    {
        // End-to-end pipeline pin: YAML semantic values → encoder →
        // centroid floats. Computing the expected centroid directly via
        // ModeVectorEncoder.Encode is the load-bearing assertion that
        // YAML carries semantic signals (not raw floats) and that the
        // encoder is the single source of truth for the YAML → vector
        // step. If the YAML seed for navigation ever drifts away from
        // (GET + navigate + no upgrade) this test catches it cheaply
        // before drift starts moving the persisted centroid.
        var layout = IdentityVectorLayout.DefaultV1();
        var source = new YamlBrowserModeSeedSource(layout);

        var seeds = source.LoadYamlSeeds();
        var nav = seeds.SingleOrDefault(s => s.ModeId == "navigation");

        nav.Should().NotBeNull();
        var expected = ModeVectorEncoder.Encode(
            layout,
            method:           "GET",
            secFetchMode:     "navigate",
            upgradeWebsocket: false);
        nav!.SeedCentroid.Should().Equal(expected);
        nav.SeedCentroid.Any(v => v != 0f).Should().BeTrue();
    }

    [Fact]
    public void LoadYamlSeeds_unknown_sentinel_has_zero_centroid()
    {
        // The unknown row carries no seed: block in YAML; the loader
        // must produce an all-zero centroid for it so the nearest-
        // centroid sentinel semantics survive. A regression where the
        // loader defaulted an unset SecFetchMode to bitmask 0 (and
        // wrote BitmaskScaled=-1) would put unknown at the same
        // coordinate as bot-raw and break the classifier's bot-raw
        // separation.
        var layout = IdentityVectorLayout.DefaultV1();
        var source = new YamlBrowserModeSeedSource(layout);

        var seeds = source.LoadYamlSeeds();
        var unknown = seeds.SingleOrDefault(s => s.ModeId == "unknown");

        unknown.Should().NotBeNull();
        unknown!.SeedCentroid.Should().AllSatisfy(v => v.Should().Be(0f));
    }
}
