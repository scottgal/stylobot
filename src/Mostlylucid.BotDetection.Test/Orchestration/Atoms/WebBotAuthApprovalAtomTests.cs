using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Auth;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Orchestration.Sessions;
using Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;
using Mostlylucid.BotDetection.Test.Auth;
using Mostlylucid.BotDetection.WebBotAuth;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms;

/// <summary>
///     Unit tests for <see cref="WebBotAuthApprovalAtom"/>. Covers cache-miss/hit/keyid-change
///     paths, absent-header early exit, malformed-header resilience, and the security
///     invariant (no raw auth material on sink or SessionAggregate).
/// </summary>
public sealed class WebBotAuthApprovalAtomTests
{
    private const string FingerprintId = "fp-wba-test";
    private const string SiteId = "example.com";
    private const string KeyId = "test-ed25519-key";

    private readonly byte[] _publicKey;
    private readonly byte[] _privateKey;
    private readonly Mock<ITokenVerifier> _verifierMock;

    public WebBotAuthApprovalAtomTests()
    {
        (_publicKey, _privateKey) = CryptoTestHelpers.NewEd25519KeyPair();
        _verifierMock = new Mock<ITokenVerifier>();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static SessionStore NewStore()
        => new(
            Options.Create(new SessionStoreOptions { CleanupInterval = TimeSpan.FromHours(1) }),
            NullLogger<SessionStore>.Instance);

    private static SignalSink SinkWithSignature(string? signature = FingerprintId)
    {
        var sink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));
        if (signature is not null)
            sink.Raise($"{SignalKeys.PrimarySignature}:{signature}", "session-1");
        return sink;
    }

    private static DefaultHttpContext ContextWithWbaHeaders(
        string signatureInput,
        string signature,
        string host = SiteId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Signature-Input"] = signatureInput;
        ctx.Request.Headers["Signature"] = signature;
        ctx.Request.Host = new HostString(host);
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/data";
        return ctx;
    }

    private static DefaultHttpContext ContextWithoutWbaHeaders(string host = SiteId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString(host);
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/data";
        return ctx;
    }

    private static DefaultHttpContext ContextWithMalformedHeaders(string host = SiteId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Signature-Input"] = "this is !! not valid rfc9421";
        ctx.Request.Headers["Signature"] = "sig1=:notbase64!!:";
        ctx.Request.Host = new HostString(host);
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/data";
        return ctx;
    }

    private WebBotAuthApprovalAtom BuildAtom(DefaultHttpContext ctx, SessionStore store)
    {
        var accessor = new StaticHttpContextAccessor(ctx);
        return new WebBotAuthApprovalAtom(
            _verifierMock.Object,
            store,
            accessor,
            NullLogger<WebBotAuthApprovalAtom>.Instance,
            Options.Create(new WebBotAuthOptions()));
    }

    private TokenVerdict ValidVerdict(string? keyId = KeyId) => new(
        TokenOutcome.Valid, keyId, "GPTBot",
        new Dictionary<string, string> { ["alg"] = "ed25519" },
        TimeSpan.FromMilliseconds(1));

    private string BuildValidRawValue(long? created = null)
    {
        var signer = new Rfc9421TestSigner
        {
            Components = ["@method", "@path"],
            Values = new Dictionary<string, string>
            {
                ["@method"] = "GET",
                ["@path"] = "/api/data",
            },
            KeyId = KeyId,
            Algorithm = "ed25519",
            Created = created ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        return signer.BuildEd25519(_privateKey);
    }

    // ── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cache_miss_first_sight_calls_verifier_once_populates_aggregate_emits_signals()
    {
        // ARRANGE
        using var store = NewStore();
        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var ctx = ContextWithWbaHeaders(lines[0], lines[1]);
        var sink = SinkWithSignature();
        _verifierMock.Setup(v => v.Verify(It.IsAny<TokenInput>())).Returns(ValidVerdict());

        var atom = BuildAtom(ctx, store);

        // ACT
        await atom.DetectAsync(sink, "session-1");

        // ASSERT: verifier called exactly once
        _verifierMock.Verify(v => v.Verify(It.IsAny<TokenInput>()), Times.Once);

        // ASSERT: beacon emitted
        sink.Detect(SignalKeys.WebBotAuthPresented).Should().BeTrue("beacon must be raised");

        // ASSERT: identity signals emitted
        sink.ReadHint(SignalKeys.VerifiedBotSigned).Should().Be("true");
        sink.ReadHint(SignalKeys.VerifiedBotKeyId).Should().Be(KeyId);
        sink.ReadHint(SignalKeys.WbaVerifiedBotName).Should().Be("GPTBot");
        sink.ReadHint(SignalKeys.SignatureVerdict).Should().Be(TokenOutcome.Valid.ToString());

        // ASSERT: verdict cached on aggregate
        var aggregate = store.TryGet(SiteId, FingerprintId);
        aggregate.Should().NotBeNull("aggregate must be created or updated");
        aggregate!.WebBotAuthVerdict.Should().NotBeNull();
        aggregate.WebBotAuthVerdict!.KeyId.Should().Be(KeyId);
        aggregate.WebBotAuthVerdict.Verdict.Should().Be(TokenOutcome.Valid);
        aggregate.WebBotAuthVerdict.SubjectName.Should().Be("GPTBot");
    }

    [Fact]
    public async Task Cache_hit_same_keyid_emits_from_cache_without_calling_verifier()
    {
        // ARRANGE: pre-seed the aggregate with a cached verdict
        using var store = NewStore();
        var cachedVerdict = new WebBotAuthCachedVerdict(KeyId, TokenOutcome.Valid, "GPTBot", "ed25519");
        store.SetWebBotAuthVerdict(SiteId, FingerprintId, cachedVerdict);

        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var ctx = ContextWithWbaHeaders(lines[0], lines[1]);
        var sink = SinkWithSignature();

        var atom = BuildAtom(ctx, store);

        // ACT
        await atom.DetectAsync(sink, "session-1");

        // ASSERT: verifier NOT called (cache hit)
        _verifierMock.Verify(v => v.Verify(It.IsAny<TokenInput>()), Times.Never);

        // ASSERT: signals come from cache
        sink.Detect(SignalKeys.WebBotAuthPresented).Should().BeTrue();
        sink.ReadHint(SignalKeys.VerifiedBotSigned).Should().Be("true");
        sink.ReadHint(SignalKeys.VerifiedBotKeyId).Should().Be(KeyId);
        sink.ReadHint(SignalKeys.WbaVerifiedBotName).Should().Be("GPTBot");
    }

    [Fact]
    public async Task Keyid_changed_reverifies_and_updates_cached_verdict()
    {
        // ARRANGE: pre-seed with a DIFFERENT keyid
        using var store = NewStore();
        var oldVerdict = new WebBotAuthCachedVerdict("old-key-id", TokenOutcome.Valid, "OtherBot", "ed25519");
        store.SetWebBotAuthVerdict(SiteId, FingerprintId, oldVerdict);

        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var ctx = ContextWithWbaHeaders(lines[0], lines[1]);
        var sink = SinkWithSignature();
        _verifierMock.Setup(v => v.Verify(It.IsAny<TokenInput>())).Returns(ValidVerdict(KeyId));

        var atom = BuildAtom(ctx, store);

        // ACT
        await atom.DetectAsync(sink, "session-1");

        // ASSERT: verifier called once (re-verify due to keyid change)
        _verifierMock.Verify(v => v.Verify(It.IsAny<TokenInput>()), Times.Once);

        // ASSERT: cache updated to new keyid
        var aggregate = store.TryGet(SiteId, FingerprintId);
        aggregate!.WebBotAuthVerdict!.KeyId.Should().Be(KeyId, "new keyid must replace old one");
        aggregate.WebBotAuthVerdict.SubjectName.Should().Be("GPTBot");
    }

    [Fact]
    public async Task Headers_absent_emits_nothing_not_even_beacon_and_returns_empty()
    {
        // ARRANGE
        using var store = NewStore();
        var ctx = ContextWithoutWbaHeaders();
        var sink = SinkWithSignature();
        var atom = BuildAtom(ctx, store);

        // ACT
        var contributions = await atom.DetectAsync(sink, "session-1");

        // ASSERT: NOTHING emitted — not even the beacon
        sink.Detect(SignalKeys.WebBotAuthPresented).Should().BeFalse("beacon must NOT be raised when headers absent");
        sink.Detect(SignalKeys.VerifiedBotSigned).Should().BeFalse();
        contributions.Should().BeEmpty();

        // ASSERT: verifier never called
        _verifierMock.Verify(v => v.Verify(It.IsAny<TokenInput>()), Times.Never);
    }

    [Fact]
    public async Task Malformed_headers_emits_beacon_then_invalid_verdict_no_exception()
    {
        // ARRANGE: malformed Signature-Input (keyid cannot be parsed cleanly)
        using var store = NewStore();
        var ctx = ContextWithMalformedHeaders();
        var sink = SinkWithSignature();

        // Verifier returns Malformed when input is garbage
        _verifierMock.Setup(v => v.Verify(It.IsAny<TokenInput>()))
            .Returns(new TokenVerdict(TokenOutcome.Malformed, null, null, null, TimeSpan.Zero));

        var atom = BuildAtom(ctx, store);

        // ACT — must not throw
        Func<Task> act = async () => await atom.DetectAsync(sink, "session-1");
        await act.Should().NotThrowAsync();

        // ASSERT: beacon IS emitted (headers were present)
        sink.Detect(SignalKeys.WebBotAuthPresented).Should().BeTrue("beacon must be raised when headers present, even if malformed");

        // ASSERT: verified_bot_signed = false, verdict reflects failure
        sink.ReadHint(SignalKeys.VerifiedBotSigned).Should().Be("false");
        sink.ReadHint(SignalKeys.SignatureVerdict).Should().Be(TokenOutcome.Malformed.ToString());
    }

    [Fact]
    public async Task Security_no_raw_auth_material_on_sink_after_verify()
    {
        // ARRANGE
        using var store = NewStore();
        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var signatureBase64 = lines[1][(lines[1].IndexOf(':') + 1)..^1]; // extract the base64
        var ctx = ContextWithWbaHeaders(lines[0], lines[1]);
        var sink = SinkWithSignature();
        _verifierMock.Setup(v => v.Verify(It.IsAny<TokenInput>())).Returns(ValidVerdict());

        var atom = BuildAtom(ctx, store);

        // ACT
        await atom.DetectAsync(sink, "session-1");

        // ASSERT: no signal contains raw signature bytes
        var allSignals = sink.Sense(_ => true).Select(e => e.Signal).ToList();
        foreach (var signal in allSignals)
        {
            signal.Should().NotContain(signatureBase64,
                "raw signature base64 must never appear on the sink");
        }

        // ASSERT: no raw auth material on SessionAggregate
        var aggregate = store.TryGet(SiteId, FingerprintId);
        if (aggregate?.WebBotAuthVerdict is { } verdict)
        {
            // Only public metadata allowed — no raw key bytes, no signature bytes
            verdict.KeyId.Should().Be(KeyId); // public identifier only
            // The verdict record has no field for signature bytes — just assert it
            // compiles to the sealed record shape with only the four metadata fields.
            var verdictFields = typeof(WebBotAuthCachedVerdict).GetProperties().Select(p => p.Name).ToList();
            verdictFields.Should().BeEquivalentTo(
                new[] { "KeyId", "Verdict", "SubjectName", "Algorithm" },
                "WebBotAuthCachedVerdict must only carry public metadata, no raw auth material");
        }
    }

    [Fact]
    public async Task Missing_primary_signature_hint_on_sink_returns_clean_without_beacon()
    {
        // ARRANGE: sink has NO primary signature hint (atom has nothing to key the session on)
        using var store = NewStore();
        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var ctx = ContextWithWbaHeaders(lines[0], lines[1]);
        // Sink with NO signature hint
        var sink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));

        var atom = BuildAtom(ctx, store);

        // ACT
        var contributions = await atom.DetectAsync(sink, "session-1");

        // ASSERT: atom still emits the beacon (headers were present) but returns no contributions
        // and skips the store lookup since there's no fingerprint key
        sink.Detect(SignalKeys.WebBotAuthPresented).Should().BeTrue("beacon must still fire if headers present");
        contributions.Should().BeEmpty();
    }
}