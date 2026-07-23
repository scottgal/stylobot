using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Unit tests for <see cref="DefaultNavVisibilityPolicy" /> -- the FOSS hidden-nav-links seam.
///     Visibility-only: this policy never touches routing/detection, it only decides whether a
///     sidebar row is drawn. Also pins that <c>_SidebarV2.cshtml</c> actually wires the seam (source
///     assertion, mirroring <see cref="SignatureDetailVerdictMergeTests" />'s style for the same
///     partial-source-check pattern).
/// </summary>
public sealed class NavVisibilityPolicyTests
{
    private static IOptions<NavVisibilityOptions> Options(params string[] hiddenPaths)
    {
        var opts = new NavVisibilityOptions { HiddenPaths = hiddenPaths.ToList() };
        return Microsoft.Extensions.Options.Options.Create(opts);
    }

    [Fact]
    public void Glob_match_hides_the_row_for_a_non_privileged_viewer()
    {
        // Per GlobToRegexCompiler semantics, "**" matches zero-or-more characters at any depth
        // (including slashes), so "purchase**" hides both the top-level row ("purchase") and
        // every nested sub-row ("purchase/checkout") in one pattern.
        var policy = new DefaultNavVisibilityPolicy(Options("purchase**", "membership**"));

        Assert.False(policy.IsVisible("purchase", isPrivilegedViewer: false));
        Assert.False(policy.IsVisible("purchase/checkout", isPrivilegedViewer: false));
    }

    [Fact]
    public void No_match_leaves_the_row_visible()
    {
        var policy = new DefaultNavVisibilityPolicy(Options("purchase**", "membership**"));

        Assert.True(policy.IsVisible("traffic", isPrivilegedViewer: false));
        Assert.True(policy.IsVisible("visitors", isPrivilegedViewer: false));
    }

    [Fact]
    public void Empty_config_shows_everything()
    {
        var policy = new DefaultNavVisibilityPolicy(Options());

        Assert.True(policy.IsVisible("traffic", isPrivilegedViewer: false));
        Assert.True(policy.IsVisible("purchase", isPrivilegedViewer: false));
    }

    [Fact]
    public void Missing_config_section_shows_everything()
    {
        // NavVisibilityOptions.HiddenPaths defaults to an empty list when the "Dashboard" config
        // section has no HiddenPaths key at all -- same code path as Empty_config_shows_everything,
        // pinned separately because it's the actual FOSS out-of-the-box scenario (no appsettings
        // entry, not an explicit empty array).
        var policy = new DefaultNavVisibilityPolicy(Microsoft.Extensions.Options.Options.Create(new NavVisibilityOptions()));

        Assert.True(policy.IsVisible("purchase", isPrivilegedViewer: false));
    }

    [Fact]
    public void Privileged_viewer_bypasses_a_matching_pattern()
    {
        var policy = new DefaultNavVisibilityPolicy(Options("purchase**"));

        // Same pattern that hides the row for a non-privileged viewer (see
        // Glob_match_hides_the_row_for_a_non_privileged_viewer) is bypassed entirely once
        // isPrivilegedViewer is true -- one bypass tier, no further gating.
        Assert.True(policy.IsVisible("purchase", isPrivilegedViewer: true));
        Assert.True(policy.IsVisible("purchase/checkout", isPrivilegedViewer: true));
    }

    [Fact]
    public void Path_is_case_insensitively_matched()
    {
        var policy = new DefaultNavVisibilityPolicy(Options("Purchase"));

        Assert.False(policy.IsVisible("purchase", isPrivilegedViewer: false));
    }

    private static string LocatePartial(string filename)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!,
            "src", "Mostlylucid.BotDetection.UI", "Views", "StyloBot", "Dashboard", filename);
        Assert.True(File.Exists(path), $"Expected partial at {path}");
        return path;
    }

    [Fact]
    public void SidebarV2_injects_and_calls_NavVisibility_IsVisible()
    {
        var source = File.ReadAllText(LocatePartial("_SidebarV2.cshtml"));
        Assert.Contains("@inject INavVisibilityPolicy NavVisibility", source);
        Assert.Contains("NavVisibility.IsVisible(\"traffic\", Model.IsPrivilegedViewer)", source);
        Assert.Contains("NavVisibility.IsVisible(\"visitors\", Model.IsPrivilegedViewer)", source);
        Assert.Contains("NavVisibility.IsVisible(\"site\", Model.IsPrivilegedViewer)", source);
        Assert.Contains("NavVisibility.IsVisible(\"policies\", Model.IsPrivilegedViewer)", source);
        Assert.Contains("NavVisibility.IsVisible(\"configuration\", Model.IsPrivilegedViewer)", source);
        Assert.Contains("NavVisibility.IsVisible(\"compliance\", Model.IsPrivilegedViewer)", source);
        // Pack rows + pack sub-rows both go through the same seam.
        Assert.Contains("NavVisibility.IsVisible(pack.Id, Model.IsPrivilegedViewer)", source);
        Assert.Contains("NavVisibility.IsVisible($\"{pack.Id}/{sub.Id}\", Model.IsPrivilegedViewer)", source);
    }
}
