using System.Security.Cryptography;
using NSec.Cryptography;

namespace Mostlylucid.BotDetection.Test.Auth;

/// <summary>
///     Real key + signature generation for the token-verifier tests. Ed25519 via
///     NSec (the same library the production crypto validator uses); ECDSA-P256
///     via <see cref="ECDsa"/>. No fakes — the verifiers are exercised against
///     genuine signatures so a broken signature-base reconstruction is caught.
/// </summary>
internal static class CryptoTestHelpers
{
    private static readonly SignatureAlgorithm Ed25519 = SignatureAlgorithm.Ed25519;

    public static (byte[] PublicKey, byte[] PrivateKey) NewEd25519KeyPair()
    {
        using var key = Key.Create(Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        return (
            key.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            key.Export(KeyBlobFormat.RawPrivateKey));
    }

    public static byte[] SignEd25519(byte[] data, byte[] rawPrivateKey)
    {
        using var key = Key.Import(Ed25519, rawPrivateKey, KeyBlobFormat.RawPrivateKey);
        return Ed25519.Sign(key, data);
    }

    public static string SignEd25519Base64(string message, byte[] rawPrivateKey)
        => Convert.ToBase64String(SignEd25519(System.Text.Encoding.UTF8.GetBytes(message), rawPrivateKey));

    /// <summary>
    ///     A P-256 signer + its SubjectPublicKeyInfo (SPKI) DER — the encoding the
    ///     registry stores for <c>ecdsa-p256-sha256</c> keys. Caller disposes the
    ///     returned <see cref="ECDsa"/>.
    /// </summary>
    public static (byte[] Spki, ECDsa Signer) NewEcdsaP256()
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ec.ExportSubjectPublicKeyInfo(), ec);
    }

    public static byte[] SignEcdsaP256(ECDsa signer, byte[] data)
        // RFC 9421 carries ECDSA signatures in raw (r||s) IEEE P1363 form.
        => signer.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}