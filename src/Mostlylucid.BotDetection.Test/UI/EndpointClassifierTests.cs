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
    [InlineData("/_stylobot/endpoints", EndpointOwner.Stylobot)]  // /_ framework convention
    [InlineData("/_stylobot/signature/abc", EndpointOwner.Stylobot)]
    [InlineData("/stylobot/hub", EndpointOwner.Stylobot)]         // always-on package mount + SignalR hub
    [InlineData("/stylobot/traffic", EndpointOwner.Stylobot)]
    [InlineData("/", EndpointOwner.Upstream)]
    [InlineData("/pricing", EndpointOwner.Upstream)]
    [InlineData("/docs/getting-started", EndpointOwner.Upstream)]
    [InlineData("/dashboardish", EndpointOwner.Upstream)]      // prefix but not a segment boundary
    [InlineData("/stylobotics", EndpointOwner.Upstream)]       // not the /stylobot mount (boundary)
    public void Classify_assigns_owner_by_basepath_and_framework_roots(string path, EndpointOwner expected)
        => EndpointClassifier.Classify(path, BasePath).Should().Be(expected);

    [Theory]
    [InlineData("/api/v1/status", true)]
    [InlineData("/api", true)]
    [InlineData("/foo/api/bar", true)]
    [InlineData("/v1/logs", true)]            // OTLP ingest — versioned API prefix
    [InlineData("/v2/traces", true)]
    [InlineData("/opentelemetry.proto.collector.logs.v1.LogsService/Export", true)] // gRPC dotted service
    [InlineData("/verify", false)]            // 'v' word, not a version segment
    [InlineData("/pricing", false)]
    [InlineData("/", false)]
    public void IsApiPath_matches_api_versioned_and_grpc(string path, bool expected)
        => EndpointClassifier.IsApiPath(path).Should().Be(expected);

    [Theory]
    [InlineData("GET", "/pricing", true)]      // human page view
    [InlineData("HEAD", "/", true)]            // head is still a page probe
    [InlineData("POST", "/pricing", false)]    // form submit, not a page
    [InlineData("POST", "/v1/logs", false)]    // OTLP ingest
    [InlineData("GET", "/v1/logs", false)]     // versioned API even via GET
    [InlineData("GET", "/dashboard/traffic", false)] // StyloBot's own UI (configured mount)
    [InlineData("GET", "/stylobot/hub", false)] // StyloBot SignalR hub (always-on mount)
    [InlineData("GET", "/_stylobot/endpoints", false)] // StyloBot partial (/_ convention)
    [InlineData("GET", "/app.js", false)]      // static asset
    public void IsContentPageRequest_is_get_upstream_page(string method, string path, bool expected)
        => EndpointClassifier.IsContentPageRequest(method, path, BasePath).Should().Be(expected);

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

    // MODE filter (Si2 endpoint-IA unification): the endpoint list's MODE chip
    // classifies each (method,path) aggregate row into one of five buckets purely
    // from the path/method shape -- the aggregate carries no per-request transport
    // signal (IdentityBrowserMode lives on fingerprints/sessions, not endpoint
    // rows), so this is a path heuristic, not the composite-browser-mode taxonomy.
    [Theory]
    [InlineData("GET", "/pricing", EndpointMode.Content)]
    [InlineData("HEAD", "/", EndpointMode.Content)]
    [InlineData("POST", "/pricing", EndpointMode.Other)]       // form submit: not a page, not API-shaped
    [InlineData("GET", "/api/v1/status", EndpointMode.Api)]
    [InlineData("POST", "/api/v1/status", EndpointMode.Api)]
    [InlineData("GET", "/v1/logs", EndpointMode.Api)]
    [InlineData("GET", "/site.js", EndpointMode.Static)]
    [InlineData("GET", "/img/logo.png", EndpointMode.Static)]
    [InlineData("GET", "/stylobot/hub", EndpointMode.Realtime)]
    [InlineData("POST", "/stylobot/hub/negotiate", EndpointMode.Realtime)]
    [InlineData("GET", "/app/hub", EndpointMode.Realtime)]
    public void ClassifyMode_buckets_by_path_shape(string method, string path, EndpointMode expected)
        => EndpointClassifier.ClassifyMode(method, path).Should().Be(expected);

    [Fact]
    public void ClassifyMode_null_or_empty_path_is_other()
    {
        EndpointClassifier.ClassifyMode("GET", null).Should().Be(EndpointMode.Other);
        EndpointClassifier.ClassifyMode("GET", "").Should().Be(EndpointMode.Other);
    }

    [Theory]
    [InlineData(EndpointMode.Content, "content")]
    [InlineData(EndpointMode.Api, "api")]
    [InlineData(EndpointMode.Static, "static")]
    [InlineData(EndpointMode.Realtime, "realtime")]
    [InlineData(EndpointMode.Other, "other")]
    public void ModeToken_round_trips_to_lowercase_url_token(EndpointMode mode, string token)
        => EndpointClassifier.ModeToken(mode).Should().Be(token);
}