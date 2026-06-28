using FluentAssertions;
using Mostlylucid.BotDetection.UI.Dashboard;

namespace Mostlylucid.BotDetection.Test.Dashboard;

public class DashboardRoutingEdgeCasesTests
{
    // ── ParseRowRef ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("traffic",  "traffic")]
    [InlineData("TRAFFIC",  "traffic")]
    [InlineData("Traffic",  "traffic")]
    public void ParseRowRef_normalises_area_to_lowercase(string input, string expected)
    {
        DashboardRoutingHelpers.ParseRowRef(input).Area.Should().Be(expected);
    }

    /// <summary>
    ///     Three or more path segments: ParseRowRef takes the first two (area + sub)
    ///     and discards the rest. IsDashboardRowPath is the guard that rejects these
    ///     paths before Resolve is called -- ParseRowRef itself is a lenient extractor.
    /// </summary>
    [Theory]
    [InlineData("a/b/c",   "a", "b")]
    [InlineData("a/b/c/d", "a", "b")]
    public void ParseRowRef_three_plus_segments_takes_first_two_area_and_sub(
        string path, string expectedArea, string expectedSub)
    {
        var result = DashboardRoutingHelpers.ParseRowRef(path);
        result.Area.Should().Be(expectedArea);
        result.Sub.Should().Be(expectedSub);
    }

    /// <summary>A path of just "/" is treated as empty -- default row.</summary>
    [Fact]
    public void ParseRowRef_single_slash_returns_default()
    {
        // M2: the default row is "traffic" (the V2 landing page), not "overview".
        var result = DashboardRoutingHelpers.ParseRowRef("/");
        result.Area.Should().Be("traffic");
        result.Sub.Should().BeNull();
    }

    /// <summary>Sub is null when there is only one segment.</summary>
    [Fact]
    public void ParseRowRef_single_segment_leaves_sub_null()
    {
        var result = DashboardRoutingHelpers.ParseRowRef("visitors");
        result.Area.Should().Be("visitors");
        result.Sub.Should().BeNull();
    }

    /// <summary>Both area and sub are lowercased independently.</summary>
    [Fact]
    public void ParseRowRef_normalises_both_area_and_sub_to_lowercase()
    {
        var result = DashboardRoutingHelpers.ParseRowRef("ASPNET-PACK/LOG-SINK");
        result.Area.Should().Be("aspnet-pack");
        result.Sub.Should().Be("log-sink");
    }

    // ── IsDashboardRowPath ────────────────────────────────────────────────────

    /// <summary>
    ///     The "setup" prefix guard must fire even when the segment has a trailing
    ///     slash (e.g. the FOSS setup wizard path "setup/wizard").
    /// </summary>
    [Theory]
    [InlineData("setup/wizard")]
    [InlineData("login/callback")]
    [InlineData("hub/negotiate")]
    [InlineData("static/js/app.js")]
    public void IsDashboardRowPath_rejects_excluded_prefixes(string rel)
    {
        DashboardRoutingHelpers.IsDashboardRowPath(rel).Should().BeFalse();
    }

    /// <summary>Three segments must not sneak through even if they look like nav paths.</summary>
    [Theory]
    [InlineData("pack/sub/extra")]
    [InlineData("a/b/c")]
    public void IsDashboardRowPath_rejects_three_plus_segments(string rel)
    {
        DashboardRoutingHelpers.IsDashboardRowPath(rel).Should().BeFalse();
    }

    // ── StripTabParam ─────────────────────────────────────────────────────────

    /// <summary>tab= comparison must be case-insensitive per the OrdinalIgnoreCase filter.</summary>
    [Theory]
    [InlineData("?TAB=traffic")]
    [InlineData("?Tab=visitors")]
    [InlineData("?tAb=site")]
    public void StripTabParam_is_case_insensitive_on_tab_key(string qs)
    {
        DashboardRoutingHelpers.StripTabParam(qs).Should().Be("");
    }

    /// <summary>A parameter whose key only begins with "tab" but is not exactly "tab=" must survive.</summary>
    [Theory]
    [InlineData("?tabindex=3",    "?tabindex=3")]
    [InlineData("?tablength=10",  "?tablength=10")]
    public void StripTabParam_keeps_params_that_start_with_tab_but_are_not_tab_key(string input, string expected)
    {
        DashboardRoutingHelpers.StripTabParam(input).Should().Be(expected);
    }

    /// <summary>No leading "?" in input -- function should still strip correctly.</summary>
    [Fact]
    public void StripTabParam_handles_input_without_leading_question_mark()
    {
        DashboardRoutingHelpers.StripTabParam("tab=traffic&fp=abc").Should().Be("?fp=abc");
    }

    /// <summary>Multiple tab= occurrences (malformed query) -- both stripped.</summary>
    [Fact]
    public void StripTabParam_strips_all_occurrences_of_tab_key()
    {
        DashboardRoutingHelpers.StripTabParam("?tab=x&tab=y&fp=z").Should().Be("?fp=z");
    }

    // ── DashboardRowRegistry ──────────────────────────────────────────────────

    /// <summary>Registry lookup uses OrdinalIgnoreCase for known FOSS rows.</summary>
    [Theory]
    [InlineData("TRAFFIC")]
    [InlineData("Traffic")]
    [InlineData("VISITORS")]
    [InlineData("Visitors")]
    [InlineData("Site")]
    public void Registry_lookup_is_case_insensitive_for_known_foss_rows(string area)
    {
        var sut = new DashboardRowRegistry(Array.Empty<IDashboardPack>());
        sut.Resolve(area, null).Should().NotBeNull();
    }

    /// <summary>
    ///     Bare pack id -- no sub -- must return null. The middleware handles the 301
    ///     to the first sub-row before calling Resolve, so this must never resolve.
    /// </summary>
    [Fact]
    public void Registry_bare_pack_id_does_not_resolve()
    {
        var pack = new SingleSubPack("my-pack", "one");
        var sut  = new DashboardRowRegistry(new[] { pack });
        sut.Resolve("my-pack", null).Should().BeNull();
    }

    /// <summary>
    ///     Two packs with sub-rows whose ids are identical must resolve
    ///     independently via their composite "pack/sub" key.
    /// </summary>
    [Fact]
    public void Registry_two_packs_with_same_sub_id_resolve_independently()
    {
        var packA = new SingleSubPack("pack-a", "routes");
        var packB = new SingleSubPack("pack-b", "routes");
        var sut   = new DashboardRowRegistry(new IDashboardPack[] { packA, packB });

        sut.Resolve("pack-a", "routes")!.Pack!.Id.Should().Be("pack-a");
        sut.Resolve("pack-b", "routes")!.Pack!.Id.Should().Be("pack-b");
    }

    /// <summary>
    ///     A pack sub-row id that collides with a FOSS row id must still resolve
    ///     correctly when requested under its own "pack/sub" composite key.
    /// </summary>
    [Fact]
    public void Registry_pack_subrow_id_collision_with_foss_row_resolves_correctly()
    {
        // "traffic" exists as a FOSS row; using it as a sub-row id under a pack
        // must not overwrite the FOSS entry or fail to resolve.
        var pack = new SingleSubPack("my-pack", "traffic");
        var sut  = new DashboardRowRegistry(new[] { pack });

        // FOSS row still resolves under bare key (V2 Traffic landing page wrapper).
        sut.Resolve("traffic", null)!.PartialPath.Should().EndWith("_Traffic.cshtml");

        // Pack sub-row resolves under composite key
        sut.Resolve("my-pack", "traffic")!.Pack!.Id.Should().Be("my-pack");
    }

    /// <summary>
    ///     Legacy hidden rows must resolve (for deep-link compatibility) but
    ///     should not appear in any group's visible Rows list.
    /// </summary>
    [Theory]
    [InlineData("countries")]
    [InlineData("identities")]
    [InlineData("clusters")]
    public void Registry_legacy_rows_resolve_but_are_not_in_visible_groups(string legacyId)
    {
        var sut = new DashboardRowRegistry(Array.Empty<IDashboardPack>());

        sut.Resolve(legacyId, null).Should().NotBeNull("legacy rows must keep deep-links alive");

        var inVisibleGroups = sut.Groups
            .SelectMany(g => g.Rows)
            .Any(r => r.Id == legacyId);
        inVisibleGroups.Should().BeFalse("legacy rows must not appear in nav");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class SingleSubPack : IDashboardPack
    {
        public SingleSubPack(string id, string subId)
        {
            Id      = id;
            SubRows = [new DashboardSubRow(subId, subId, subId)];
        }

        public string Id { get; }
        public string Label => Id;
        public string Icon  => "bx bx-cube";
        public IReadOnlyList<DashboardSubRow> SubRows { get; }
    }
}
