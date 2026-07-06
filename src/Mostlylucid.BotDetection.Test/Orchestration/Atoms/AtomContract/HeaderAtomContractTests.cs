using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;

/// <summary>
///     Contract lock-in for <see cref="HeaderAtom"/>: the signals it actually raises
///     must match the emit contract its manifest (<c>header.detector.yaml</c>) declares.
///     Drift here silently corrupts the dashboard's "which detector emitted this signal"
///     provenance (the <c>Source</c> column) and the SignalCatalog.
///
///     First vertical slice of task #27; the pattern is validated here before it is
///     generalised to a <c>[Theory]</c> across every converted atom.
///
///     <para>
///         The manifest's <c>emits.on_start</c> block (<c>detector.header.started</c>) is
///         deliberately NOT asserted: no atom body raises it — it was the deleted
///         BlackboardOrchestrator's per-atom tracing wrapper, and Step 7 removed the
///         orchestrator without pruning the manifests. Only <c>emits.on_complete</c> is
///         the atom's own emission surface.
///     </para>
/// </summary>
public sealed class HeaderAtomContractTests
{
    [Fact]
    public async Task Emits_every_signal_the_manifest_declares_under_on_complete()
    {
        // HeaderAtom's emits are almost all unconditional (they raise a true/false/empty
        // value regardless of header presence). The two exceptions — the population-rate
        // signals — only raise when Accept / Accept-Language are ABSENT. So a single
        // maximal stimulus can't cover them. Run against a minimal stimulus (trips the
        // population branch) AND a maximal one (trips any present-required path) and union.
        var raised = new HashSet<string>(StringComparer.Ordinal);
        raised.UnionWith(await RaisedKeysAsync(MinimalBrowserRequest()));
        raised.UnionWith(await RaisedKeysAsync(MaximalRequest()));

        var loader = new DetectorManifestLoader();
        loader.LoadEmbeddedManifests();
        var manifest = loader.GetDetectorManifest("HeaderContributor")
            ?? throw new InvalidOperationException("Manifest 'HeaderContributor' not found in embedded resources.");

        var declared = manifest.Emits.OnComplete
            .Select(s => s.Key)
            .ToHashSet(StringComparer.Ordinal);

        var missing = declared.Except(raised).OrderBy(k => k).ToList();
        missing.Should().BeEmpty(
            "HeaderAtom must raise every key its manifest declares under emits.on_complete " +
            "(drift corrupts dashboard signal provenance). Declared-but-never-raised: {0}",
            string.Join(", ", missing));
    }

    /// <summary>
    ///     Runs HeaderAtom in isolation against one crafted request and returns the
    ///     distinct signal keys it raised (the <c>:value</c> suffix stripped).
    /// </summary>
    private static async Task<IReadOnlyCollection<string>> RaisedKeysAsync(HttpContext http)
    {
        var atom = new HeaderAtom(
            NullLogger<HeaderAtom>.Instance,
            new StubDetectorConfigProvider(),
            new DeploymentNormTracker(),
            new StaticHttpContextAccessor(http));

        var sink = new SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));
        await atom.DetectAsync(sink, sessionId: "test");

        return sink.Sense()
            .Select(e => e.Signal.Split(':', 2)[0])
            .ToHashSet(StringComparer.Ordinal);
    }

    // Browser UA with NO Accept / Accept-Language / Sec-Fetch / api-key so the
    // population-rate emit branch (guarded on those being absent) fires.
    private static HttpContext MinimalBrowserRequest()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        return http;
    }

    // Everything present — trips any path that needs a header to actually exist.
    private static HttpContext MaximalRequest()
    {
        var http = new DefaultHttpContext();
        var h = http.Request.Headers;
        h.UserAgent =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        h["Accept"] = "text/html";
        h["Accept-Language"] = "en-US";
        h["Accept-Encoding"] = "gzip, deflate, br";
        h["Sec-Fetch-Site"] = "same-origin";
        h["Sec-Fetch-Mode"] = "navigate";
        h["Sec-Fetch-Dest"] = "document";
        h["X-Forwarded-For"] = "1.2.3.4";
        h["Service-Worker"] = "script";
        h["Connection"] = "Upgrade";
        h["Upgrade"] = "websocket";
        h["Sec-WebSocket-Key"] = "dGhlIHNhbXBsZSBub25jZQ==";
        return http;
    }

    private sealed class StaticHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    /// <summary>Returns the caller-supplied default for every lookup — the atom exercises
    /// its code-default behaviour, which is all the emit contract cares about.</summary>
    private sealed class StubDetectorConfigProvider : IDetectorConfigProvider
    {
        public DetectorManifest? GetManifest(string detectorName) => null;
        public DetectorDefaults GetDefaults(string detectorName) => new();
        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue) => defaultValue;

        public Task<T> GetParameterAsync<T>(string detectorName, string parameterName,
            ConfigResolutionContext context, T defaultValue, CancellationToken ct = default)
            => Task.FromResult(defaultValue);

        public void InvalidateCache(string? detectorName = null) { }

        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }
}
