using FluentAssertions;
using Mostlylucid.BotDetection.UI.Helpers;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Locks in the owner + content classification the traffic overview's
///     "Top content pages" widget relies on. The widget shows UPSTREAM CONTENT
///     ONLY — StyloBot's own dashboard/API paths, API endpoints, and static
///     assets must all be excluded — and the endpoints control badges each row's
///     owner. If any of these regress, the widget silently shows the wrong rows.
/// </summary>
public class EndpointClassifierTests
{
    private const string BasePath = "/dashboard";

    [Theory]
    [InlineData("/dashboard", EndpointOwner.Stylobot)]        // exact base
    [InlineData("/dashboard/traffic", EndpointOwner.Stylobot)] // under base
    [InlineData("/dashboard/site/endpoint", EndpointOwner.Stylobot)]
    [InlineData("/_content/Mostlylucid.BotDetection.UI/x.js", EndpointOwner.Stylobot)]
    [InlineData("/_blazor", EndpointOwner.Stylobot)]
    [InlineData("/", EndpointOwner.Upstream)]
    [InlineData("/pricing", EndpointOwner.Upstream)]
    [InlineData("/docs/getting-started", EndpointOwner.Upstream)]
    [InlineData("/dashboardish", EndpointOwner.Upstream)]      // prefix but not a segment boundary
    public void Classify_assigns_owner_by_basepath_and_framework_roots(string path, EndpointOwner expected)
        => EndpointClassifier.Classify(path, BasePath).Should().Be(expected);

    [Theory]
    [InlineData("/api/v1/status", true)]
    [InlineData("/api", true)]
    [InlineData("/foo/api/bar", true)]
    [InlineData("/pricing", false)]
    [InlineData("/", false)]
    public void IsApiPath_matches_api_segments(string path, bool expected)
        => EndpointClassifier.IsApiPath(path).Should().Be(expected);

    [Theory]
    [InlineData("/site.js", true)]
    [InlineData("/css/app.css", true)]
    [InlineData("/img/logo.png", true)]
    [InlineData("/pricing", false)]
    public void IsStaticResource_matches_asset_extensions(string path, bool expected)
        => EndpointClassifier.IsStaticResource(path).Should().Be(expected);

    [Theory]
    [InlineData("/pricing", true)]            // upstream page
    [InlineData("/", true)]                   // upstream root
    [InlineData("/dashboard/traffic", false)] // StyloBot's own UI
    [InlineData("/api/v1/status", false)]     // API endpoint
    [InlineData("/site.js", false)]           // static asset
    [InlineData("/_content/x/app.css", false)]// StyloBot asset (owner + static)
    public void IsUpstreamContent_is_upstream_non_api_non_static(string path, bool expected)
        => EndpointClassifier.IsUpstreamContent(path, BasePath).Should().Be(expected);

    [Fact]
    public void Empty_or_null_path_is_upstream_not_content()
    {
        EndpointClassifier.Classify(null, BasePath).Should().Be(EndpointOwner.Upstream);
        EndpointClassifier.IsUpstreamContent("", BasePath).Should().BeFalse();
    }
}