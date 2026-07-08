using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mostlylucid.BotDetection.Auth;
using Mostlylucid.BotDetection.WebBotAuth;

namespace Mostlylucid.BotDetection.Test.Auth;

/// <summary>
///     Unit tests for <see cref="Rfc9421SignatureVerifier"/>. Covers the locked
///     error taxonomy (Valid / InvalidSignature / Expired / UnknownKey / Malformed
///     / MissingKey) against real Ed25519 and ECDSA-P256 signatures, plus a
///     hand-written signature-base "oracle" that pins the base reconstruction
///     independently of the verifier's own logic.
/// </summary>
public sealed class Rfc9421SignatureVerifierTests
{
    private const string KeyId = "test-key-ed25519";
    private static readonly DateTimeOffset Now = new(2021, 4, 20, 2, 7, 55, TimeSpan.Zero);

    private readonly byte[] _pub;
    private readonly byte[] _priv;
    private readonly PublicKeyRegistry _registry = new();
    private readonly FakeTimeProvider _time = new(Now);

    public Rfc9421SignatureVerifierTests()
    {
        (_pub, _priv) = CryptoTestHelpers.NewEd25519KeyPair();
        _registry.SeedManual([new PublicKeyEntry(KeyId, "GPTBot", _pub, "ed25519", null, "test")]);
    }

    private Rfc9421SignatureVerifier Make(TokenVerifierOptions? opts = null)
        => new(_registry, new CryptoSignatureValidator(), Options.Create(opts ?? new TokenVerifierOptions()), _time);

    // Standard covered set: derived @method/@path come from the TokenInput; @authority
    // + content-type come from CoveredHeaders.
    private static readonly string[] Components = ["@method", "@path", "@authority", "content-type"];

    private static readonly Dictionary<string, string> ResolvedValues = new()
    {
        ["@method"] = "POST",
        ["@path"] = "/foo",
        ["@authority"] = "example.com",
        ["content-type"] = "application/json"
    };

    private static Dictionary<string, string> CoveredHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["@authority"] = "example.com",
        ["content-type"] = "application/json"
    };

    private Rfc9421TestSigner Signer(long? created = null, long? expires = null, string? alg = "ed25519")
        => new()
        {
            Components = Components,
            Values = ResolvedValues,
            KeyId = KeyId,
            Algorithm = alg,
            Created = created,
            Expires = expires
        };

    private static TokenInput Input(string rawValue, IReadOnlyDictionary<string, string>? headers = null)
        => new(TokenKind.Rfc9421HttpSignature, rawValue, headers ?? CoveredHeaders(), "POST", "/foo");

    // ── Valid ────────────────────────────────────────────────────────────────

    [Fact]
    public void Valid_ed25519_signature_verifies()
    {
        var raw = Signer(created: Now.ToUnixTimeSeconds()).BuildEd25519(_priv);

        var verdict = Make().Verify(Input(raw));

        verdict.Outcome.Should().Be(TokenOutcome.Valid);
        verdict.KeyId.Should().Be(KeyId);
        verdict.SubjectName.Should().Be("GPTBot");
        verdict.Claims.Should().ContainKey("alg").WhoseValue.Should().Be("ed25519");
        verdict.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void Reconstructs_signature_base_exactly_against_a_hand_written_oracle()
    {
        // The oracle: the signature base written out by hand per RFC 9421 §2.5,
        // NOT via the verifier's reconstruction. If the verifier rebuilds a
        // different base, the Ed25519 verify fails and this test catches it.
        const string paramsValue =
            "(\"@method\" \"@path\" \"@authority\" \"content-type\");created=1618884475;keyid=\"test-key-ed25519\";alg=\"ed25519\"";
        const string oracleBase =
            "\"@method\": POST\n" +
            "\"@path\": /foo\n" +
            "\"@authority\": example.com\n" +
            "\"content-type\": application/json\n" +
            "\"@signature-params\": " + paramsValue;

        var sig = Convert.ToBase64String(CryptoTestHelpers.SignEd25519(Encoding.UTF8.GetBytes(oracleBase), _priv));
        var raw = "sig1=" + paramsValue + "\n" + "sig1=:" + sig + ":";

        Make().Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.Valid);
    }

    [Fact]
    public void Valid_ecdsa_p256_signature_verifies()
    {
        var (spki, signer) = CryptoTestHelpers.NewEcdsaP256();
        using var _ = signer;
        _registry.SeedManual([new PublicKeyEntry("ec-key", "PerplexityBot", spki, "ecdsa-p256-sha256", null, "test")]);

        var s = new Rfc9421TestSigner
        {
            Components = Components,
            Values = ResolvedValues,
            KeyId = "ec-key",
            Algorithm = "ecdsa-p256-sha256",
            Created = Now.ToUnixTimeSeconds()
        };
        var sig = CryptoTestHelpers.SignEcdsaP256(signer, Encoding.UTF8.GetBytes(s.SignatureBase()));
        var raw = s.Pack(Convert.ToBase64String(sig));

        Make().Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.Valid);
    }

    [Fact]
    public void Missing_alg_param_falls_back_to_key_algorithm()
    {
        var raw = Signer(created: Now.ToUnixTimeSeconds(), alg: null).BuildEd25519(_priv);

        Make().Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.Valid);
    }

    // ── InvalidSignature ───────────────────────────────────────────────────────

    [Fact]
    public void Tampered_signature_is_InvalidSignature()
    {
        var s = Signer(created: Now.ToUnixTimeSeconds());
        var sig = CryptoTestHelpers.SignEd25519(Encoding.UTF8.GetBytes(s.SignatureBase()), _priv);
        sig[0] ^= 0xFF;
        var raw = s.Pack(Convert.ToBase64String(sig));

        Make().Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.InvalidSignature);
    }

    [Fact]
    public void Authority_mismatch_breaks_the_base_and_is_InvalidSignature()
    {
        var raw = Signer(created: Now.ToUnixTimeSeconds()).BuildEd25519(_priv);

        // Verifier resolves @authority from a DIFFERENT host than was signed.
        var headers = CoveredHeaders();
        headers["@authority"] = "evil.example";

        Make().Verify(Input(raw, headers)).Outcome.Should().Be(TokenOutcome.InvalidSignature);
    }

    // ── Expired ────────────────────────────────────────────────────────────────

    [Fact]
    public void Signature_with_past_expires_is_Expired()
    {
        var raw = Signer(created: Now.AddHours(-2).ToUnixTimeSeconds(),
                         expires: Now.AddHours(-1).ToUnixTimeSeconds()).BuildEd25519(_priv);

        Make().Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.Expired);
    }

    [Fact]
    public void Signature_older_than_MaxSignatureAge_is_Expired()
    {
        var raw = Signer(created: Now.AddHours(-2).ToUnixTimeSeconds()).BuildEd25519(_priv);
        var opts = new TokenVerifierOptions { MaxSignatureAge = TimeSpan.FromMinutes(30) };

        Make(opts).Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.Expired);
    }

    // ── UnknownKey ─────────────────────────────────────────────────────────────

    [Fact]
    public void Keyid_not_in_registry_is_UnknownKey()
    {
        var s = new Rfc9421TestSigner
        {
            Components = Components, Values = ResolvedValues,
            KeyId = "nobody-knows-me", Algorithm = "ed25519", Created = Now.ToUnixTimeSeconds()
        };
        var raw = s.BuildEd25519(_priv);

        Make().Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.UnknownKey);
    }

    [Fact]
    public void Key_past_its_not_after_is_UnknownKey()
    {
        _registry.SeedManual([new PublicKeyEntry(KeyId, "GPTBot", _pub, "ed25519", Now.AddHours(-1), "test")]);
        var raw = Signer(created: Now.ToUnixTimeSeconds()).BuildEd25519(_priv);

        Make().Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.UnknownKey);
    }

    // ── MissingKey ─────────────────────────────────────────────────────────────

    [Fact]
    public void No_keyid_param_is_MissingKey()
    {
        // Hand-craft a Signature-Input with no keyid parameter.
        const string paramsValue = "(\"@method\" \"@path\");created=1618884475";
        var oracleBase = "\"@method\": POST\n\"@path\": /foo\n\"@signature-params\": " + paramsValue;
        var sig = Convert.ToBase64String(CryptoTestHelpers.SignEd25519(Encoding.UTF8.GetBytes(oracleBase), _priv));
        var raw = "sig1=" + paramsValue + "\nsig1=:" + sig + ":";

        Make().Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.MissingKey);
    }

    // ── Malformed ──────────────────────────────────────────────────────────────

    [Fact]
    public void Garbage_raw_value_is_Malformed()
    {
        Make().Verify(Input("this is not a signature")).Outcome.Should().Be(TokenOutcome.Malformed);
    }

    [Fact]
    public void Missing_covered_header_value_is_Malformed()
    {
        var raw = Signer(created: Now.ToUnixTimeSeconds()).BuildEd25519(_priv);

        // Drop content-type so the verifier cannot resolve a covered component.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["@authority"] = "example.com"
        };

        Make().Verify(Input(raw, headers)).Outcome.Should().Be(TokenOutcome.Malformed);
    }

    [Fact]
    public void Disallowed_algorithm_is_Malformed()
    {
        var raw = Signer(created: Now.ToUnixTimeSeconds()).BuildEd25519(_priv);
        var opts = new TokenVerifierOptions { AllowedAlgorithms = ["ecdsa-p256-sha256"] };

        Make(opts).Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.Malformed);
    }

    [Fact]
    public void RequireCreated_with_no_created_is_Malformed()
    {
        var s = new Rfc9421TestSigner
        {
            Components = Components, Values = ResolvedValues,
            KeyId = KeyId, Algorithm = "ed25519", Created = null
        };
        var raw = s.BuildEd25519(_priv);
        var opts = new TokenVerifierOptions { RequireCreated = true };

        Make(opts).Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.Malformed);
    }
}
