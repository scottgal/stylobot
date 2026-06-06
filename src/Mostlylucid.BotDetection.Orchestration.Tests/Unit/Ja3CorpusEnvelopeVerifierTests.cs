using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Definitions.TlsReference;
using NSec.Cryptography;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Verifies the Ed25519 corpus-envelope signing contract. Generates a fresh
///     keypair per test (NSec is fast enough to do this in setup), signs a
///     known YAML body, and asserts the verifier accepts the good signature,
///     rejects tampering, and rejects substitution under the wrong key.
/// </summary>
public class Ja3CorpusEnvelopeVerifierTests
{
    private readonly Ja3CorpusEnvelopeVerifier _verifier = new(NullLogger<Ja3CorpusEnvelopeVerifier>.Instance);

    private const string SampleCorpusYaml = """
        version: 1
        generated_at: '2026-06-06'
        references:
          chrome:
            120:
              desktop:
                ja3: 'cd08e31494f9531f560d64c695473da9'
                ja3_string: '771,4865-4866,0-23,29-23-24,0'
        """;

    private static (string publicKeyBase64, byte[] privateKey) MintKeyPair()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var creationParams = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        using var key = Key.Create(algorithm, creationParams);
        var publicKeyBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var privateKeyBytes = key.Export(KeyBlobFormat.RawPrivateKey);
        return (Convert.ToBase64String(publicKeyBytes), privateKeyBytes);
    }

    private static string Sign(byte[] privateKeyBytes, string body)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        using var key = Key.Import(algorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey);
        var sig = algorithm.Sign(key, Encoding.UTF8.GetBytes(body));
        return Convert.ToBase64String(sig);
    }

    [Fact]
    public void Verify_GoodSignature_Accepts()
    {
        var (pub, priv) = MintKeyPair();
        var envelope = new Ja3CorpusEnvelope
        {
            CorpusYaml = SampleCorpusYaml,
            Signature = Sign(priv, SampleCorpusYaml),
            GeneratedAt = "2026-06-06T00:00:00Z",
            KeyId = "test-v1"
        };

        Assert.True(_verifier.Verify(envelope, pub));
    }

    [Fact]
    public void Verify_TamperedBody_Rejects()
    {
        // Signature signs the original body; the envelope advertises a
        // mutated body. Verifier must reject -- this is the canonical
        // "attacker rewrote the corpus to add a malicious reference"
        // threat model.
        var (pub, priv) = MintKeyPair();
        var signature = Sign(priv, SampleCorpusYaml);
        var tampered = SampleCorpusYaml.Replace("chrome:", "chrome:\n    999:\n      desktop:\n        ja3: 'evil'\n        ja3_string: '0'");
        var envelope = new Ja3CorpusEnvelope { CorpusYaml = tampered, Signature = signature };

        Assert.False(_verifier.Verify(envelope, pub));
    }

    [Fact]
    public void Verify_WrongPublicKey_Rejects()
    {
        // Sign with one keypair, verify against another's public key.
        // Catches the "attacker substituted their own envelope" case.
        var (_, priv) = MintKeyPair();
        var (otherPub, _) = MintKeyPair();
        var envelope = new Ja3CorpusEnvelope
        {
            CorpusYaml = SampleCorpusYaml,
            Signature = Sign(priv, SampleCorpusYaml)
        };

        Assert.False(_verifier.Verify(envelope, otherPub));
    }

    [Fact]
    public void Verify_MissingSignature_Rejects()
    {
        var (pub, _) = MintKeyPair();
        var envelope = new Ja3CorpusEnvelope { CorpusYaml = SampleCorpusYaml };

        Assert.False(_verifier.Verify(envelope, pub));
    }

    [Fact]
    public void Verify_MissingCorpus_Rejects()
    {
        var (pub, priv) = MintKeyPair();
        var envelope = new Ja3CorpusEnvelope
        {
            Signature = Sign(priv, SampleCorpusYaml)
        };

        Assert.False(_verifier.Verify(envelope, pub));
    }

    [Fact]
    public void Verify_MalformedPublicKey_Rejects()
    {
        var (_, priv) = MintKeyPair();
        var envelope = new Ja3CorpusEnvelope
        {
            CorpusYaml = SampleCorpusYaml,
            Signature = Sign(priv, SampleCorpusYaml)
        };

        Assert.False(_verifier.Verify(envelope, "not-a-base64-key"));
        Assert.False(_verifier.Verify(envelope, "")); // empty
        Assert.False(_verifier.Verify(envelope, Convert.ToBase64String(new byte[16]))); // wrong length
    }
}
