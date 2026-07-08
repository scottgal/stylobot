using System.Security.Cryptography;
using NSecPublicKey = NSec.Cryptography.PublicKey;
using NSecSignatureAlgorithm = NSec.Cryptography.SignatureAlgorithm;
using NSecKeyBlobFormat = NSec.Cryptography.KeyBlobFormat;

namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Default <see cref="ISignatureValidator"/>: Ed25519 via NSec (already a FOSS
///     dependency) and ECDSA-P256-SHA256 via <see cref="ECDsa"/>. Stateless
///     singleton. Never throws — a malformed key, wrong-length signature, or
///     unknown algorithm all return <c>false</c>.
/// </summary>
public sealed class CryptoSignatureValidator : ISignatureValidator
{
    public const string Ed25519 = "ed25519";
    public const string EcdsaP256Sha256 = "ecdsa-p256-sha256";

    private static readonly NSecSignatureAlgorithm Ed25519Algorithm = NSecSignatureAlgorithm.Ed25519;

    /// <inheritdoc />
    public bool Supports(string algorithm) => Normalize(algorithm) is Ed25519 or EcdsaP256Sha256;

    /// <inheritdoc />
    public bool Verify(string algorithm, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
    {
        switch (Normalize(algorithm))
        {
            case Ed25519:
                return VerifyEd25519(data, signature, publicKey);
            case EcdsaP256Sha256:
                return VerifyEcdsaP256(data, signature, publicKey);
            default:
                return false;
        }
    }

    private static bool VerifyEd25519(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
    {
        try
        {
            var pk = NSecPublicKey.Import(Ed25519Algorithm, publicKey, NSecKeyBlobFormat.RawPublicKey);
            return Ed25519Algorithm.Verify(pk, data, signature);
        }
        catch
        {
            // Bad key length / import failure — treat as a failed verification.
            return false;
        }
    }

    private static bool VerifyEcdsaP256(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(publicKey, out _);
            // RFC 9421 ECDSA signatures are raw r||s (IEEE P1363), not DER.
            return ec.VerifyData(data, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string? algorithm) => algorithm?.Trim().ToLowerInvariant() ?? "";
}
