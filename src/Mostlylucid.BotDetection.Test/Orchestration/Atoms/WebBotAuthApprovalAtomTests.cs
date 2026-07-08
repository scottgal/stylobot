using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Auth;
using Mostlylucid.BotDetection.Identity;
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

    private WebBotAuthApprovalAtom BuildAtom(DefaultHttpContext ctx, SessionStore store,
        IdentityArchetypeRegistry? registry = null)
    {
        var accessor = new StaticHttpContextAccessor(ctx);
        return new WebBotAuthApprovalAtom(
            _verifierMock.Object,
            store,
            accessor,
            NullLogger<WebBotAuthApprovalAtom>.Instance,
            Options.Create(new WebBotAuthOptions()),
            registry ?? EmptyRegistry());
    }

    private static IdentityArchetypeRegistry EmptyRegistry()
    {
        var reg = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance,
            new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1()));
        reg.Replace(Array.Empty<IdentityArchetype>());
        return reg;
    }

    private static IdentityArchetypeRegistry RegistryWith(string archetypeId)
    {
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        var centroid = new float[dim]; // all-zero start so any nudge is measurable
        var archetype = new IdentityArchetype
        {
            ArchetypeId = archetypeId,
            Name = archetypeId,
            ArchetypeKind = "ai-bot",
            Centroid = centroid,
            DimensionMask = new float[dim],
        };
        var reg = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance,
            new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1()));
        reg.Replace(new[] { archetype });
        return reg;
    }

    private static float[] NonZeroVector()
    {
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        var v = new float[dim];
        Array.Fill(v, 0.5f);
        return v;
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
    public async Task Cache_hit_same_keyid_and_signature_emits_from_cache_without_calling_verifier()
    {
        // ARRANGE: pre-seed the aggregate with a cached verdict whose signature
        // hash matches the presented signature (genuine re-presentation).
        using var store = NewStore();
        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var cachedVerdict = new WebBotAuthCachedVerdict(
            KeyId, TokenOutcome.Valid, "GPTBot", "ed25519",
            WebBotAuthApprovalAtom.ComputeSignatureHash(rawValue));
        store.SetWebBotAuthVerdict(SiteId, FingerprintId, cachedVerdict);

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
    public async Task Cache_rejected_when_signature_differs_even_with_same_keyid_reverifies()
    {
        // ARRANGE: pre-seed a verdict with the SAME keyid but a DIFFERENT
        // signature hash. A request reusing the (public) keyid with a different
        // (tampered/replayed/expired) signature must NOT be served the cached
        // "verified" verdict — it must re-run crypto.
        using var store = NewStore();
        var staleVerdict = new WebBotAuthCachedVerdict(
            KeyId, TokenOutcome.Valid, "GPTBot", "ed25519",
            WebBotAuthApprovalAtom.ComputeSignatureHash("a-different-signature-entirely"));
        store.SetWebBotAuthVerdict(SiteId, FingerprintId, staleVerdict);

        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var ctx = ContextWithWbaHeaders(lines[0], lines[1]);
        var sink = SinkWithSignature();
        _verifierMock.Setup(v => v.Verify(It.IsAny<TokenInput>())).Returns(ValidVerdict(KeyId));

        var atom = BuildAtom(ctx, store);

        // ACT
        await atom.DetectAsync(sink, "session-1");

        // ASSERT: verifier IS called — the signature-hash mismatch defeats the
        // keyid-only cache hit, closing the replay/tamper hole.
        _verifierMock.Verify(v => v.Verify(It.IsAny<TokenInput>()), Times.Once);

        // ASSERT: cache updated to the freshly-verified signature hash.
        var aggregate = store.TryGet(SiteId, FingerprintId);
        aggregate!.WebBotAuthVerdict!.SignatureHash
            .Should().Be(WebBotAuthApprovalAtom.ComputeSignatureHash(rawValue));
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

            // SignatureHash is a non-reversible digest, NOT the signature: it must
            // never equal the raw base64 signature that was on the wire.
            verdict.SignatureHash.Should().NotBeNullOrEmpty();
            verdict.SignatureHash.Should().NotContain(signatureBase64,
                "SignatureHash must be a non-reversible digest, never the raw signature");

            // The verdict record carries only public metadata + the non-reversible
            // signature digest — assert the sealed record shape has exactly these.
            var verdictFields = typeof(WebBotAuthCachedVerdict).GetProperties().Select(p => p.Name).ToList();
            verdictFields.Should().BeEquivalentTo(
                new[] { "KeyId", "Verdict", "SubjectName", "Algorithm", "SignatureHash" },
                "WebBotAuthCachedVerdict must only carry public metadata + a non-reversible digest");
        }
    }

    [Fact]
    public async Task Missing_primary_signature_still_verifies_and_emits_but_skips_cache()
    {
        // ARRANGE: sink has NO primary signature hint (nothing to key the session
        // cache on). Verification must NOT be gated on the cache key existing.
        using var store = NewStore();
        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var ctx = ContextWithWbaHeaders(lines[0], lines[1]);
        // Sink with NO signature hint
        var sink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));
        _verifierMock.Setup(v => v.Verify(It.IsAny<TokenInput>())).Returns(ValidVerdict());

        var atom = BuildAtom(ctx, store);

        // ACT
        var contributions = await atom.DetectAsync(sink, "session-1");

        // ASSERT: beacon fired, verifier STILL ran, and identity signals emitted
        // even though there is no fingerprint to cache against.
        sink.Detect(SignalKeys.WebBotAuthPresented).Should().BeTrue("beacon must still fire if headers present");
        _verifierMock.Verify(v => v.Verify(It.IsAny<TokenInput>()), Times.Once,
            "verification must run even when no primary signature is available to cache against");
        sink.ReadHint(SignalKeys.VerifiedBotSigned).Should().Be("true");
        sink.ReadHint(SignalKeys.VerifiedBotKeyId).Should().Be(KeyId);
        contributions.Should().BeEmpty();

        // ASSERT: nothing was written to the session store (no fingerprint key).
        store.TryGet(SiteId, FingerprintId).Should().BeNull("no cache write without a fingerprint key");
    }

    // ── archetype nudge tests ─────────────────────────────────────────────────

    [Fact]
    public async Task Verified_result_nudges_verified_bot_archetype_centroid()
    {
        // ARRANGE: seed a registry with the verified-GPTBot archetype (all-zero centroid)
        const string SubjectName = "GPTBot";
        var archetypeId = $"verified-{SubjectName}";
        var registry = RegistryWith(archetypeId);
        var centroidBefore = (float[])registry.TryGetById(archetypeId)!.Centroid.Clone();

        using var store = NewStore();
        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var ctx = ContextWithWbaHeaders(lines[0], lines[1]);

        // Place a non-zero identity vector in HttpContext.Items as IdentityVectorAtom would
        var identityVector = NonZeroVector();
        ctx.Items[IdentityVectorAtom.VectorKey] = identityVector;

        var sink = SinkWithSignature();
        _verifierMock.Setup(v => v.Verify(It.IsAny<TokenInput>()))
            .Returns(new TokenVerdict(TokenOutcome.Valid, KeyId, SubjectName,
                new Dictionary<string, string> { ["alg"] = "ed25519" }, TimeSpan.Zero));

        var atom = BuildAtom(ctx, store, registry);

        // ACT
        await atom.DetectAsync(sink, "session-1");

        // ASSERT: centroid moved toward the identity vector
        var centroidAfter = registry.TryGetById(archetypeId)!.Centroid;
        centroidAfter.Should().NotEqual(centroidBefore,
            "a verified result must nudge the archetype centroid toward the observed identity vector");
        // No-clobber: centroid must not equal the raw vector
        centroidAfter.Should().NotEqual(identityVector,
            "the nudge is bounded EMA, not a hard-replace of the centroid");
    }

    [Fact]
    public async Task Unverified_result_does_not_nudge_archetype_centroid()
    {
        // ARRANGE: seed a registry with the verified-GPTBot archetype
        const string SubjectName = "GPTBot";
        var archetypeId = $"verified-{SubjectName}";
        var registry = RegistryWith(archetypeId);
        var centroidBefore = (float[])registry.TryGetById(archetypeId)!.Centroid.Clone();

        using var store = NewStore();
        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var ctx = ContextWithWbaHeaders(lines[0], lines[1]);
        ctx.Items[IdentityVectorAtom.VectorKey] = NonZeroVector();

        var sink = SinkWithSignature();
        // Verifier returns an invalid-signature outcome (e.g., tampered or wrong key)
        _verifierMock.Setup(v => v.Verify(It.IsAny<TokenInput>()))
            .Returns(new TokenVerdict(TokenOutcome.InvalidSignature, KeyId, SubjectName, null, TimeSpan.Zero));

        var atom = BuildAtom(ctx, store, registry);

        // ACT
        await atom.DetectAsync(sink, "session-1");

        // ASSERT: centroid unchanged
        registry.TryGetById(archetypeId)!.Centroid.Should().Equal(centroidBefore,
            "an unverified (invalid) outcome must not nudge the archetype centroid");
    }

    [Fact]
    public async Task Nudge_is_skipped_when_identity_vector_is_absent_from_context()
    {
        // ARRANGE: verified result but no identity vector in HttpContext.Items
        const string SubjectName = "GPTBot";
        var archetypeId = $"verified-{SubjectName}";
        var registry = RegistryWith(archetypeId);
        var centroidBefore = (float[])registry.TryGetById(archetypeId)!.Centroid.Clone();

        using var store = NewStore();
        var rawValue = BuildValidRawValue();
        var lines = rawValue.Split('\n');
        var ctx = ContextWithWbaHeaders(lines[0], lines[1]);
        // Intentionally NOT setting ctx.Items[IdentityVectorAtom.VectorKey]

        var sink = SinkWithSignature();
        _verifierMock.Setup(v => v.Verify(It.IsAny<TokenInput>()))
            .Returns(new TokenVerdict(TokenOutcome.Valid, KeyId, SubjectName,
                new Dictionary<string, string> { ["alg"] = "ed25519" }, TimeSpan.Zero));

        var atom = BuildAtom(ctx, store, registry);

        // ACT — must not throw
        Func<Task> act = async () => await atom.DetectAsync(sink, "session-1");
        await act.Should().NotThrowAsync();

        // ASSERT: centroid untouched (no vector available to nudge with)
        registry.TryGetById(archetypeId)!.Centroid.Should().Equal(centroidBefore,
            "with no identity vector available the nudge must be skipped rather than using a zero vector");
    }
}