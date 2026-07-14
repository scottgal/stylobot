using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;

/// <summary>
///     Overview-ratified guards for session-mode-aware behavioral suppression (the SignalR-hub
///     false-positive). Repetition (low path-entropy) is bot-evidence in a content-browsing mode
///     and the EXPECTED baseline in a streaming mode -- so within an established-streaming
///     conversation it is neutral, NOT penalized. But deference is mode-CONSISTENCY-conditioned,
///     not a latch: a session that established streaming then scrapes (high path-entropy) re-engages
///     the penalties. And behavioral state is keyed on PrimarySignature, not the shared edge IP.
/// </summary>
public sealed class SessionModeBehavioralTests
{
    private const string RepetitiveReason = "Repeatedly visiting the same few URLs";
    private const string ScanningReason = "random scanning pattern";
    private const string BurstReason = "Burst detected";

    private static BehavioralAtom NewAtom(HttpContext http)
    {
        var opts = Options.Create(new BotDetectionOptions()); // EnableAdvancedPatternDetection = true
        var cache = new MemoryCache(new MemoryCacheOptions());
        var detector = new BehavioralDetector(NullLogger<BehavioralDetector>.Instance, opts, cache);
        return new BehavioralAtom(
            NullLogger<BehavioralAtom>.Instance, detector, cache, opts,
            new StubDetectorConfigProvider(), new StaticHttpContextAccessor(http));
    }

    private static async Task<IReadOnlyList<DetectionContribution>> Hit(
        BehavioralAtom atom, HttpContext http, string signature, string path, bool sessionStreaming)
    {
        http.Request.Path = path;
        var sink = new SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.PrimarySignature}:{signature}", "s");
        sink.Raise($"{SignalKeys.ClientIp}:203.0.113.5", "s"); // one shared edge IP for every request
        if (sessionStreaming) sink.Raise($"{SignalKeys.SessionEstablishedStreaming}:true", "s");
        return await atom.DetectAsync(sink, "s");
    }

    // Build a low-but-nonzero path-entropy (repetitive) pattern: 9x hub + 1x other -> entropy ~0.47 (< 0.5).
    private static async Task<IReadOnlyList<DetectionContribution>> BuildRepetitive(
        BehavioralAtom atom, HttpContext http, string sig, bool sessionStreaming)
    {
        for (var i = 0; i < 9; i++) await Hit(atom, http, sig, "/stylobot/hub", sessionStreaming);
        return await Hit(atom, http, sig, "/stylobot/status", sessionStreaming);
    }

    [Fact]
    public async Task Established_streaming_session_neutralizes_the_repetition_penalty()
    {
        // The reported /stylobot/hub case: an established SignalR conversation polling repetitively
        // must NOT be scored repetitive-scraper.
        var http = new DefaultHttpContext();
        var last = await BuildRepetitive(NewAtom(http), http, "sig-hub", sessionStreaming: true);

        last.Should().NotContain(c => c.Reason.Contains(RepetitiveReason),
            "repetition is the expected baseline within a genuinely-streaming conversation");
    }

    [Fact]
    public async Task No_established_mode_leaves_the_repetition_penalty_firing()
    {
        // Proves it's the SESSION MODE doing the work, not the path: same repetitive pattern, no
        // established streaming -> the penalty fires.
        var http = new DefaultHttpContext();
        var last = await BuildRepetitive(NewAtom(http), http, "sig-plain", sessionStreaming: false);

        last.Should().Contain(c => c.Reason.Contains(RepetitiveReason),
            "without an established streaming mode, repetitive polling is still scored");
    }

    [Fact]
    public async Task Established_streaming_that_flips_to_scraping_re_engages_the_penalty()
    {
        // Anti-latch: a session that established streaming then scans many distinct paths is mode-
        // INCONSISTENT -> the high-entropy scanning penalty fires despite SessionEstablishedStreaming.
        var http = new DefaultHttpContext();
        var atom = NewAtom(http);
        IReadOnlyList<DetectionContribution> last = Array.Empty<DetectionContribution>();
        for (var i = 0; i < 14; i++)
            last = await Hit(atom, http, "sig-flip", $"/content/{i}", sessionStreaming: true);

        last.Should().Contain(c => c.Reason.Contains(ScanningReason),
            "content-scraping is mode-inconsistent with streaming; deference is withdrawn, not a latch");
    }

    [Fact]
    public async Task Behavioral_state_is_keyed_on_signature_not_the_shared_edge_ip()
    {
        // Two clients behind one edge share a peer IP. A burst under signature A must NOT taint a
        // fresh request under signature B (proves the re-key off clientIp -> PrimarySignature).
        var http = new DefaultHttpContext();
        var atom = NewAtom(http);
        for (var i = 0; i < 20; i++) await Hit(atom, http, "sig-A", "/x", sessionStreaming: false);

        var b = await Hit(atom, http, "sig-B", "/y", sessionStreaming: false);

        b.Should().NotContain(c => c.Reason.Contains(BurstReason),
            "signature B (same edge IP) must not inherit signature A's burst -- behavioral state is signature-keyed");
    }

    // ── SessionModeResolverAtom (production side) ──────────────────────────────────────────────

    [Fact]
    public async Task Resolver_raises_established_streaming_for_a_session_with_a_signalr_state()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(cache, NullLogger<SessionStore>.Instance);
        await store.RecordRequestAsync("sig-sr",
            new SessionRequest(RequestState.SignalR, DateTimeOffset.UtcNow, "/stylobot/hub", 200));

        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.PrimarySignature}:sig-sr", "s");
        await new SessionModeResolverAtom(store).DetectAsync(sink, "s");

        sink.ReadBoolHint(SignalKeys.SessionEstablishedStreaming).Should().BeTrue(
            "a session whose Markov state includes SignalR is an established streaming conversation");
    }

    [Fact]
    public async Task Resolver_stays_silent_for_a_content_browsing_session()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new SessionStore(cache, NullLogger<SessionStore>.Instance);
        await store.RecordRequestAsync("sig-pv",
            new SessionRequest(RequestState.PageView, DateTimeOffset.UtcNow, "/about", 200));

        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.PrimarySignature}:sig-pv", "s");
        await new SessionModeResolverAtom(store).DetectAsync(sink, "s");

        sink.ReadBoolHint(SignalKeys.SessionEstablishedStreaming).Should().BeFalse(
            "a page-browsing session is not streaming; the signal must not fire (else it would suppress real scraper repetition)");
    }
}
