using System.Text;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;

namespace Mostlylucid.BotDetection.Definitions.TlsReference;

/// <summary>
///     Verifies an Ed25519-signed corpus envelope. The signature is over the
///     UTF-8 bytes of <see cref="Ja3CorpusEnvelope.CorpusYaml"/>; the public
///     key is the base64-encoded raw 32-byte form (NSec's
///     <c>KeyBlobFormat.RawPublicKey</c>). Constant-time comparison is handled
///     by NSec internally.
/// </summary>
public sealed class Ja3CorpusEnvelopeVerifier
{
    private readonly ILogger<Ja3CorpusEnvelopeVerifier>? _logger;

    public Ja3CorpusEnvelopeVerifier(ILogger<Ja3CorpusEnvelopeVerifier>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Returns true when the envelope's signature validates under
    ///     <paramref name="publicKeyBase64"/>. False on any failure (bad key
    ///     format, signature mismatch, throws from NSec, etc.). Logs at
    ///     Warning on rejection so refresh failures are observable.
    /// </summary>
    public bool Verify(Ja3CorpusEnvelope envelope, string publicKeyBase64)
    {
        if (envelope == null) return false;
        if (string.IsNullOrEmpty(envelope.Signature)) { Reject("missing signature"); return false; }
        if (string.IsNullOrEmpty(envelope.CorpusYaml)) { Reject("missing corpus body"); return false; }
        if (string.IsNullOrEmpty(publicKeyBase64)) { Reject("public key not configured"); return false; }

        try
        {
            var keyBytes = Convert.FromBase64String(publicKeyBase64);
            if (keyBytes.Length != 32)
            {
                Reject($"public key is {keyBytes.Length} bytes; Ed25519 raw public keys are 32 bytes");
                return false;
            }

            var sigBytes = Convert.FromBase64String(envelope.Signature);
            if (sigBytes.Length != 64)
            {
                Reject($"signature is {sigBytes.Length} bytes; Ed25519 signatures are 64 bytes");
                return false;
            }

            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, keyBytes, KeyBlobFormat.RawPublicKey);
            var bodyBytes = Encoding.UTF8.GetBytes(envelope.CorpusYaml);
            var valid = SignatureAlgorithm.Ed25519.Verify(publicKey, bodyBytes, sigBytes);
            if (!valid) Reject("signature did not validate under the configured public key");
            return valid;
        }
        catch (FormatException ex)
        {
            Reject($"base64 decode failed: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Ja3 corpus envelope verification threw");
            return false;
        }
    }

    private void Reject(string reason) =>
        _logger?.LogWarning("Rejected JA3 corpus envelope: {Reason}", reason);
}
