using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mostlylucid.BotDetection.Auth;

namespace Mostlylucid.BotDetection.Test.Auth;

/// <summary>
///     Unit tests for <see cref="SignedTokenVerifier"/> — verifies a generic
///     signed bearer token (base64 of a canonical-JSON-signed document) against
///     the configured trust anchors, then checks the configured expiry claim.
///     No licensing knowledge: the verifier surfaces raw claims and the test
///     mints with generic claim names (<c>sub</c>, <c>exp</c>). Covers the locked
///     taxonomy for the signed-bearer-token kind.
/// </summary>
public sealed class SignedTokenVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly byte[] _pub;
    private readonly byte[] _priv;
    private readonly FakeTimeProvider _time = new(Now);

    public SignedTokenVerifierTests()
    {
        (_pub, _priv) = CryptoTestHelpers.NewEd25519KeyPair();
    }

    private SignedTokenVerifier Make(params TokenTrustAnchor[] anchors)
    {
        var opts = new TokenVerifierOptions { TrustAnchors = anchors.ToList() };
        return new SignedTokenVerifier(new CryptoSignatureValidator(), Options.Create(opts), _time);
    }

    private TokenTrustAnchor Anchor(byte[]? pub = null, string name = "issuer-1")
        => new() { Name = name, PublicKey = Convert.ToBase64String(pub ?? _pub), Algorithm = "ed25519" };

    private Dictionary<string, string> Claims(DateTimeOffset expiry) => new()
    {
        ["sub"] = "acme-corp",
        ["scope"] = "read:all",
        ["exp"] = expiry.ToString("O")
    };

    private static TokenInput Input(string rawValue)
        => new(TokenKind.SignedBearerToken, rawValue,
            new Dictionary<string, string>(), "GET", "/api/premium");

    // ── Valid ────────────────────────────────────────────────────────────────

    [Fact]
    public void Valid_signed_token_verifies()
    {
        var raw = SignedTokenTestMinter.Mint(Claims(Now.AddDays(30)), _priv);

        var verdict = Make(Anchor()).Verify(Input(raw));

        verdict.Outcome.Should().Be(TokenOutcome.Valid);
        verdict.SubjectName.Should().Be("acme-corp");
        verdict.Claims.Should().ContainKey("scope").WhoseValue.Should().Be("read:all");
    }

    [Fact]
    public void Multiple_anchors_one_match_is_valid()
    {
        var (otherPub, _) = CryptoTestHelpers.NewEd25519KeyPair();
        var raw = SignedTokenTestMinter.Mint(Claims(Now.AddDays(30)), _priv);

        var verdict = Make(Anchor(otherPub, "wrong"), Anchor(_pub, "right")).Verify(Input(raw));

        verdict.Outcome.Should().Be(TokenOutcome.Valid);
    }

    [Fact]
    public void Subject_claim_name_is_configurable()
    {
        // A consumer with its own convention points the option at its own claim name.
        var fields = new Dictionary<string, string> { ["owner"] = "Acme Corp", ["exp"] = Now.AddDays(1).ToString("O") };
        var raw = SignedTokenTestMinter.Mint(fields, _priv);
        var opts = new TokenVerifierOptions
        {
            TrustAnchors = [Anchor()],
            SignedTokenSubjectClaim = "owner"
        };
        var sut = new SignedTokenVerifier(new CryptoSignatureValidator(), Options.Create(opts), _time);

        sut.Verify(Input(raw)).SubjectName.Should().Be("Acme Corp");
    }

    // ── Expired ────────────────────────────────────────────────────────────────

    [Fact]
    public void Expired_token_is_Expired()
    {
        var raw = SignedTokenTestMinter.Mint(Claims(Now.AddDays(-1)), _priv);

        Make(Anchor()).Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.Expired);
    }

    // ── MissingKey ─────────────────────────────────────────────────────────────

    [Fact]
    public void No_trust_anchors_is_MissingKey()
    {
        var raw = SignedTokenTestMinter.Mint(Claims(Now.AddDays(30)), _priv);

        Make().Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.MissingKey);
    }

    // ── InvalidSignature ───────────────────────────────────────────────────────

    [Fact]
    public void Wrong_anchor_key_is_InvalidSignature()
    {
        var (otherPub, _) = CryptoTestHelpers.NewEd25519KeyPair();
        var raw = SignedTokenTestMinter.Mint(Claims(Now.AddDays(30)), _priv);

        Make(Anchor(otherPub)).Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.InvalidSignature);
    }

    [Fact]
    public void Tampered_claim_is_InvalidSignature()
    {
        // Sign a token, then change a claim in the JSON before base64 so the
        // canonical content no longer matches the signature.
        var signed = SignedTokenTestMinter.SignedJson(Claims(Now.AddDays(30)), _priv);
        var tampered = signed.Replace("read:all", "write:all");
        var raw = SignedTokenTestMinter.ToBase64(tampered);

        Make(Anchor()).Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.InvalidSignature);
    }

    // ── Malformed ──────────────────────────────────────────────────────────────

    [Fact]
    public void Not_base64_is_Malformed()
    {
        Make(Anchor()).Verify(Input("!!! not base64 !!!")).Outcome.Should().Be(TokenOutcome.Malformed);
    }

    [Fact]
    public void Base64_of_non_json_is_Malformed()
    {
        var raw = SignedTokenTestMinter.ToBase64("this is not json");

        Make(Anchor()).Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.Malformed);
    }

    [Fact]
    public void Json_without_signature_field_is_Malformed()
    {
        var raw = SignedTokenTestMinter.ToBase64("{\"sub\":\"acme\",\"scope\":\"read\"}");

        Make(Anchor()).Verify(Input(raw)).Outcome.Should().Be(TokenOutcome.Malformed);
    }
}
