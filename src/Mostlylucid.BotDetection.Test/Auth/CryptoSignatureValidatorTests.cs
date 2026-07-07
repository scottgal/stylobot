using System.Text;
using FluentAssertions;
using Mostlylucid.BotDetection.Auth;

namespace Mostlylucid.BotDetection.Test.Auth;

/// <summary>
///     Unit tests for <see cref="CryptoSignatureValidator"/> — the single crypto
///     home used by both the RFC 9421 and capability verifiers. Ed25519 (NSec) and
///     ECDSA-P256 (SubjectPublicKeyInfo + IEEE-P1363). Never throws; a bad input is
///     a <c>false</c>, not an exception.
/// </summary>
public sealed class CryptoSignatureValidatorTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("the quick brown fox");
    private readonly ISignatureValidator _validator = new CryptoSignatureValidator();

    [Fact]
    public void Ed25519_verifies_a_valid_signature()
    {
        var (pub, priv) = CryptoTestHelpers.NewEd25519KeyPair();
        var sig = CryptoTestHelpers.SignEd25519(Data, priv);

        _validator.Verify("ed25519", Data, sig, pub).Should().BeTrue();
    }

    [Fact]
    public void Ed25519_rejects_a_tampered_signature()
    {
        var (pub, priv) = CryptoTestHelpers.NewEd25519KeyPair();
        var sig = CryptoTestHelpers.SignEd25519(Data, priv);
        sig[0] ^= 0xFF;

        _validator.Verify("ed25519", Data, sig, pub).Should().BeFalse();
    }

    [Fact]
    public void Ed25519_rejects_the_wrong_key()
    {
        var (_, priv) = CryptoTestHelpers.NewEd25519KeyPair();
        var (otherPub, _) = CryptoTestHelpers.NewEd25519KeyPair();
        var sig = CryptoTestHelpers.SignEd25519(Data, priv);

        _validator.Verify("ed25519", Data, sig, otherPub).Should().BeFalse();
    }

    [Fact]
    public void Ed25519_algorithm_name_is_case_insensitive()
    {
        var (pub, priv) = CryptoTestHelpers.NewEd25519KeyPair();
        var sig = CryptoTestHelpers.SignEd25519(Data, priv);

        _validator.Verify("ED25519", Data, sig, pub).Should().BeTrue();
    }

    [Fact]
    public void EcdsaP256_verifies_a_valid_signature()
    {
        var (spki, signer) = CryptoTestHelpers.NewEcdsaP256();
        using var _ = signer;
        var sig = CryptoTestHelpers.SignEcdsaP256(signer, Data);

        _validator.Verify("ecdsa-p256-sha256", Data, sig, spki).Should().BeTrue();
    }

    [Fact]
    public void EcdsaP256_rejects_a_tampered_signature()
    {
        var (spki, signer) = CryptoTestHelpers.NewEcdsaP256();
        using var _ = signer;
        var sig = CryptoTestHelpers.SignEcdsaP256(signer, Data);
        sig[0] ^= 0xFF;

        _validator.Verify("ecdsa-p256-sha256", Data, sig, spki).Should().BeFalse();
    }

    [Fact]
    public void Supports_is_true_for_known_algorithms_and_false_for_unknown()
    {
        _validator.Supports("ed25519").Should().BeTrue();
        _validator.Supports("ecdsa-p256-sha256").Should().BeTrue();
        _validator.Supports("rsa-pss-sha512").Should().BeFalse();
        _validator.Supports("").Should().BeFalse();
    }

    [Fact]
    public void Verify_unknown_algorithm_returns_false()
    {
        var (pub, priv) = CryptoTestHelpers.NewEd25519KeyPair();
        var sig = CryptoTestHelpers.SignEd25519(Data, priv);

        _validator.Verify("rsa-pss-sha512", Data, sig, pub).Should().BeFalse();
    }

    [Fact]
    public void Verify_malformed_key_returns_false_not_throw()
    {
        var act = () => _validator.Verify("ed25519", Data, new byte[] { 1, 2, 3 }, new byte[] { 4, 5 });
        act.Should().NotThrow();
        _validator.Verify("ed25519", Data, new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }).Should().BeFalse();
    }
}
