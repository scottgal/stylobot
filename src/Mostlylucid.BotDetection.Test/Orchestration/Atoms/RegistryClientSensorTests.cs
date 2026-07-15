using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms;

/// <summary>
///     Unit tests for <see cref="RegistryClientSensor"/>. The load-bearing test is
///     <see cref="SpoofedDockerUa_withoutV2Protocol_isNotLowered"/> - a docker UA that
///     is not doing the registry protocol must NOT get a score reduction. That negative
///     test is the proof the archetype recognizes registry clients WITHOUT a bypass.
/// </summary>
public sealed class RegistryClientSensorTests
{
    private const string Session = "session-1";

    private static readonly string DockerUa =
        "docker/24.0.7 go/go1.20.10 git-commit/311b9ff kernel/6.5.0 os/linux arch/amd64 " +
        "UpstreamClient(Docker-Client/24.0.7 (linux))";

    private const string ManifestAccept = "application/vnd.docker.distribution.manifest.v2+json";

    private static SignalSink NewSink() => new(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));

    private static RegistryClientSensor NewSensor(HttpContext ctx) => new(
        NullLogger<RegistryClientSensor>.Instance,
        new StubDetectorConfigProvider(),
        new StaticHttpContextAccessor(ctx));

    private static DefaultHttpContext Ctx(string ua, string path, string? accept = null, string? auth = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        if (!string.IsNullOrEmpty(ua)) ctx.Request.Headers.UserAgent = ua;
        if (!string.IsNullOrEmpty(accept)) ctx.Request.Headers.Accept = accept;
        if (!string.IsNullOrEmpty(auth)) ctx.Request.Headers.Authorization = auth;
        return ctx;
    }

    // ── corroborated: real docker pull ──────────────────────────────────────

    [Fact]
    public async Task DockerManifestPull_isCorroborated_lowThreatWithoutEarlyExit()
    {
        var sink = NewSink();
        var ctx = Ctx(DockerUa, "/v2/library/nginx/manifests/latest", accept: ManifestAccept);

        var result = await NewSensor(ctx).DetectAsync(sink, Session);

        result.Should().ContainSingle();
        var c = result[0];
        c.ConfidenceDelta.Should().BeLessThan(0, "a corroborated registry client biases the score toward low-threat");
        c.BotType.Should().Be(BotType.Tool.ToString());
        c.BotName.Should().Be("Docker 24.0.7");

        sink.ReadHint(SignalKeys.RegistryClientDetected).Should().Be("true");
        sink.ReadHint(SignalKeys.RegistryClientName).Should().Be("Docker");
        sink.ReadHint(SignalKeys.RegistryClientVersion).Should().Be("24.0.7");
        sink.ReadHint(SignalKeys.RegistryV2Step).Should().Be("manifest");
        sink.Detect(SignalKeys.RegistryV2Ran).Should().BeTrue();
        sink.Detect(SignalKeys.RegistryUaOnly).Should().BeFalse();
    }

    [Fact]
    public async Task V2Ping_isCorroboratedByPathAlone()
    {
        // The registry protocol opens with GET /v2/ (the 401 probe) - no Accept, no bearer yet.
        var sink = NewSink();
        var ctx = Ctx(DockerUa, "/v2/");

        var result = await NewSensor(ctx).DetectAsync(sink, Session);

        result.Should().ContainSingle();
        result[0].ConfidenceDelta.Should().BeLessThan(0);
        sink.ReadHint(SignalKeys.RegistryV2Step).Should().Be("ping");
        sink.ReadHint(SignalKeys.RegistryClientDetected).Should().Be("true");
    }

    [Fact]
    public async Task ContainerdBlobPull_isCorroborated_versionParsed()
    {
        var sink = NewSink();
        var ctx = Ctx("containerd/1.7.11+unknown", "/v2/library/alpine/blobs/sha256:deadbeef");

        var result = await NewSensor(ctx).DetectAsync(sink, Session);

        result.Should().ContainSingle();
        result[0].BotName.Should().Be("containerd 1.7.11");
        sink.ReadHint(SignalKeys.RegistryClientName).Should().Be("containerd");
        sink.ReadHint(SignalKeys.RegistryV2Step).Should().Be("blob");
    }

    [Fact]
    public async Task BareGoHttpClient_onV2Path_isCorroborated()
    {
        // The bare Go client UA is too generic on its own (requires_oci_path) but on a /v2
        // path it is a registry client.
        var sink = NewSink();
        var ctx = Ctx("Go-http-client/2.0", "/v2/repo/blobs/sha256:abc", accept: ManifestAccept);

        var result = await NewSensor(ctx).DetectAsync(sink, Session);

        result.Should().ContainSingle();
        sink.ReadHint(SignalKeys.RegistryClientName).Should().Be("Go registry client");
    }

    [Fact]
    public async Task AcceptMediaAlone_corroboratesAKnownFamily()
    {
        // Content-negotiation is behavioural truth: a docker UA asking for a registry
        // manifest media type is corroborated even off the canonical /v2/ path.
        var sink = NewSink();
        var ctx = Ctx(DockerUa, "/proxy/manifests/x", accept: ManifestAccept);

        var result = await NewSensor(ctx).DetectAsync(sink, Session);

        result.Should().ContainSingle();
        sink.Detect(SignalKeys.RegistryAcceptManifest).Should().BeTrue();
    }

    // ── the spoof guard (proof it is not a bypass) ──────────────────────────

    [Fact]
    public async Task SpoofedDockerUa_withoutV2Protocol_isNotLowered()
    {
        // A scraper sets User-Agent: docker/24 but hits a normal content path with a
        // browser Accept and no registry behaviour. It must NOT earn the low-threat
        // contribution - it is scored normally by the rest of the pipeline.
        var sink = NewSink();
        var ctx = Ctx("docker/24.0.7", "/wp-login.php", accept: "text/html,application/xhtml+xml");

        var result = await NewSensor(ctx).DetectAsync(sink, Session);

        result.Should().BeEmpty("a UA claim without registry protocol behaviour is not a registry client");
        sink.Detect(SignalKeys.RegistryUaOnly).Should().BeTrue("the spoof-suspect marker is raised for observability");
        sink.ReadHint(SignalKeys.RegistryClientDetected).Should().BeNull("registry.client is never raised on UA alone");
        sink.Detect(SignalKeys.RegistryV2Ran).Should().BeTrue("the sensor still looked (detection is not skipped)");
    }

    [Fact]
    public async Task BareGoHttpClient_offV2Path_isInvisible()
    {
        // The generic Go client on a non-registry path is not a registry client and the
        // sensor stays out of the way entirely (no ran marker, no ua_only).
        var sink = NewSink();
        var ctx = Ctx("Go-http-client/2.0", "/api/data");

        var result = await NewSensor(ctx).DetectAsync(sink, Session);

        result.Should().BeEmpty();
        sink.Detect(SignalKeys.RegistryV2Ran).Should().BeFalse();
        sink.Detect(SignalKeys.RegistryUaOnly).Should().BeFalse();
    }

    [Fact]
    public async Task RealBrowser_isIgnored()
    {
        var sink = NewSink();
        var ctx = Ctx(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36",
            "/index.html", accept: "text/html");

        var result = await NewSensor(ctx).DetectAsync(sink, Session);

        result.Should().BeEmpty();
        sink.Detect(SignalKeys.RegistryV2Ran).Should().BeFalse();
    }
}
