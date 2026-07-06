using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.HealthEndpoints;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;

/// <summary>
///     Forward emit-contract test for <see cref="HealthEndpointAtom"/>: when given a
///     request with a health-probe path the atom must raise every key its manifest
///     declares under <c>emits.on_complete</c>.
///
///     <para>
///         The reverse direction (no atom raises an UNDECLARED key) is covered
///         generically by <see cref="AtomEmitContractTests"/>. This test covers the
///         forward direction: all DECLARED keys are actually raised when the atom
///         is stimulated with a matching path.
///     </para>
/// </summary>
public sealed class HealthEndpointAtomContractTests
{
    [Fact]
    public async Task Emits_every_signal_the_manifest_declares_under_on_complete_for_health_path()
    {
        var raised = await RaisedKeysAsync(HealthPathRequest("/health"));

        var loader = new DetectorManifestLoader();
        loader.LoadEmbeddedManifests();
        var manifest = loader.GetDetectorManifest("HealthEndpoint")
            ?? throw new InvalidOperationException("Manifest 'HealthEndpoint' not found in embedded resources.");

        var declared = manifest.Emits.OnComplete
            .Select(s => s.Key)
            .ToHashSet(StringComparer.Ordinal);

        var missing = declared.Except(raised).OrderBy(k => k).ToList();
        missing.Should().BeEmpty(
            "HealthEndpointAtom must raise every key its manifest declares under emits.on_complete " +
            "(drift corrupts dashboard signal provenance). Declared-but-never-raised: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public async Task Does_not_emit_for_non_health_path()
    {
        var raised = await RaisedKeysAsync(HealthPathRequest("/api/products"));
        raised.Should().BeEmpty("atom must raise nothing for a non-health path");
    }

    private static async Task<IReadOnlyCollection<string>> RaisedKeysAsync(HttpContext http)
    {
        var catalog = new HealthEndpointCatalog(Options.Create(HealthEndpointOptions.Default));
        var atom = new HealthEndpointAtom(
            NullLogger<HealthEndpointAtom>.Instance,
            catalog,
            new StaticHttpContextAccessor(http));

        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));
        await atom.DetectAsync(sink, sessionId: "test");

        return sink.Sense()
            .Select(e => e.Signal.Split(':', 2)[0])
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HttpContext HealthPathRequest(string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = path;
        return http;
    }
}
