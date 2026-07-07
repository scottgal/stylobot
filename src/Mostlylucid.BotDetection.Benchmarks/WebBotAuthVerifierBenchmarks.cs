using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Auth;
using Mostlylucid.BotDetection.WebBotAuth;
using NSec.Cryptography;

namespace Mostlylucid.BotDetection.Benchmarks;

/// <summary>
///     Hot-path benchmarks for the Web Bot Auth verification pipeline.
///
///     Covers:
///     - Full RFC 9421 verify pipeline (parse Signature-Input/Signature, resolve key,
///       reconstruct sig base, crypto verify, freshness check) for Ed25519 and ECDSA-P256.
///     - Six RFC 9421 scenarios: valid Ed25519, valid ECDSA, tampered sig, expired, unknown key, malformed.
///     - Signed bearer token verifier (JSON parse + canonical rebuild + crypto + expiry).
///     - Crypto baseline: raw CryptoSignatureValidator.Verify(Ed25519) in isolation.
///       The delta (RFC 9421 full - crypto baseline) = parse + resolve + reconstruct overhead.
///
///     Run: dotnet run -c Release --project src/Mostlylucid.BotDetection.Benchmarks -- --filter '*WebBotAuth*'
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class WebBotAuthVerifierBenchmarks
{
    // Verifiers (internal, accessible via InternalsVisibleTo)
    private Rfc9421SignatureVerifier _rfc9421 = null!;
    private SignedTokenVerifier _signedToken = null!;
    private CryptoSignatureValidator _crypto = null!;

    // Pre-built TokenInput values (structs, no per-benchmark alloc)
    private TokenInput _validEd25519;
    private TokenInput _validEcdsa;
    private TokenInput _invalidSig;
    private TokenInput _expired;
    private TokenInput _unknownKey;
    private TokenInput _malformed;
    private TokenInput _signedTokenValid;
    private TokenInput _signedTokenTampered;

    // Raw crypto baseline
    private byte[] _ed25519Data = null!;
    private byte[] _ed25519Sig = null!;
    private byte[] _ed25519Pub = null!;

    private byte[] _ed25519Priv = null!;
    private ECDsa _ecdsaSigner = null!;

    [GlobalSetup]
    public void Setup()
    {
        // -- Ed25519 key pair --
        var edAlg = SignatureAlgorithm.Ed25519;
        using var edKey = Key.Create(edAlg, new KeyCreationParameters
            { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        _ed25519Pub = edKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _ed25519Priv = edKey.Export(KeyBlobFormat.RawPrivateKey);

        // -- ECDSA-P256 key pair --
        _ecdsaSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecdsaSpki = _ecdsaSigner.ExportSubjectPublicKeyInfo();

        // -- Registry: bench key + 48 noise keys (total ~50) --
        var entries = new List<PublicKeyEntry>
        {
            new("bench-key-ed25519", "BenchBot", _ed25519Pub.AsMemory(), "ed25519", null, "bench"),
            new("bench-key-ecdsa", "BenchBotEcdsa", ecdsaSpki.AsMemory(), "ecdsa-p256-sha256", null, "bench"),
        };
        for (var i = 0; i < 48; i++)
        {
            var (pub, _) = GenEd25519Pair();
            entries.Add(new PublicKeyEntry($"noise-{i}", $"NoiseBot{i}", pub.AsMemory(), "ed25519", null, "bench"));
        }

        var registry = new PublicKeyRegistry();
        registry.SeedManual(entries);

        _crypto = new CryptoSignatureValidator();
        var opts = Options.Create(new TokenVerifierOptions());
        _rfc9421 = new Rfc9421SignatureVerifier(registry, _crypto, opts);

        var signedOpts = Options.Create(new TokenVerifierOptions
        {
            TrustAnchors =
            [
                new TokenTrustAnchor
                {
                    Name = "BenchBot",
                    PublicKey = Convert.ToBase64String(_ed25519Pub),
                    Algorithm = "ed25519"
                }
            ]
        });
        _signedToken = new SignedTokenVerifier(_crypto, signedOpts);

        // -- Shared covered headers --
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = "bench.example.com"
        };
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // valid_ed25519
        _validEd25519 = MakeEd25519Input("bench-key-ed25519", headers, "GET", "/api/detect", now, null);

        // valid_ecdsa_p256
        _validEcdsa = MakeEcdsaInput("bench-key-ecdsa", headers, "GET", "/api/detect", now, null);

        // invalid_sig: valid structure but signature bytes tampered
        var tamperedRaw = BuildRfc9421Raw("bench-key-ed25519", "ed25519", headers, "GET", "/api/detect", now, null, _ed25519Priv);
        _invalidSig = new TokenInput(TokenKind.Rfc9421HttpSignature, TamperSignatureLine(tamperedRaw), headers, "GET", "/api/detect");

        // expired: valid sig, but expires was 400s ago (> 300s MaxClockSkew default)
        long pastNow = now - 410;
        long pastExpires = now - 400;
        _expired = MakeEd25519Input("bench-key-ed25519", headers, "GET", "/api/detect", pastNow, pastExpires);

        // unknown_key: keyid not in registry -> full O(n) scan finds nothing
        _unknownKey = MakeEd25519Input("unknown-key-xyz", headers, "GET", "/api/detect", now, null);

        // malformed: no newline -> immediate early-out
        _malformed = new TokenInput(TokenKind.Rfc9421HttpSignature, "garbage-no-valid-newline", headers, "GET", "/api/detect");

        // signed bearer token valid
        var tokenFields = new Dictionary<string, string>
        {
            ["sub"] = "BenchBot",
            ["iat"] = now.ToString(),
            ["exp"] = (now + 3600).ToString()
        };
        var validToken = MintSignedToken(tokenFields, _ed25519Priv);
        _signedTokenValid = new TokenInput(TokenKind.SignedBearerToken, validToken,
            new Dictionary<string, string>(), "POST", "/");

        // signed bearer token tampered: flip one base64 char in the "signature" field so
        // the JSON remains structurally valid (exercises the full JsonDocument-heavy path)
        // but the Ed25519 verify fails -> InvalidSignature
        _signedTokenTampered = new TokenInput(TokenKind.SignedBearerToken, TamperSignatureField(validToken),
            new Dictionary<string, string>(), "POST", "/");

        // Crypto baseline data (isolates crypto cost from parse+reconstruct)
        _ed25519Data = Encoding.UTF8.GetBytes("benchmark message for direct Ed25519 crypto verify path");
        _ed25519Sig = SignEd25519(_ed25519Data, _ed25519Priv);
    }

    [GlobalCleanup]
    public void Cleanup() => _ecdsaSigner?.Dispose();

    // ── RFC 9421 scenarios ──────────────────────────────────────────────────

    [Benchmark]
    public TokenVerdict Rfc9421_Valid_Ed25519() => _rfc9421.Verify(_validEd25519);

    [Benchmark]
    public TokenVerdict Rfc9421_Valid_EcdsaP256() => _rfc9421.Verify(_validEcdsa);

    [Benchmark]
    public TokenVerdict Rfc9421_Invalid_Sig() => _rfc9421.Verify(_invalidSig);

    [Benchmark]
    public TokenVerdict Rfc9421_Expired() => _rfc9421.Verify(_expired);

    [Benchmark]
    public TokenVerdict Rfc9421_Unknown_Key() => _rfc9421.Verify(_unknownKey);

    [Benchmark]
    public TokenVerdict Rfc9421_Malformed() => _rfc9421.Verify(_malformed);

    // ── Signed bearer token scenarios ───────────────────────────────────────

    [Benchmark]
    public TokenVerdict SignedToken_Valid() => _signedToken.Verify(_signedTokenValid);

    [Benchmark]
    public TokenVerdict SignedToken_Tampered() => _signedToken.Verify(_signedTokenTampered);

    // ── Crypto baseline (isolates Ed25519 verify cost) ──────────────────────

    [Benchmark]
    public bool Crypto_Baseline_Ed25519() =>
        _crypto.Verify("ed25519", _ed25519Data, _ed25519Sig, _ed25519Pub);

    // ── Input builders (inlined; no test-project reference) ─────────────────

    private TokenInput MakeEd25519Input(
        string keyId,
        IReadOnlyDictionary<string, string> headers,
        string method, string path,
        long created, long? expires)
    {
        var raw = BuildRfc9421Raw(keyId, "ed25519", headers, method, path, created, expires, _ed25519Priv);
        return new TokenInput(TokenKind.Rfc9421HttpSignature, raw, headers, method, path);
    }

    private TokenInput MakeEcdsaInput(
        string keyId,
        IReadOnlyDictionary<string, string> headers,
        string method, string path,
        long created, long? expires)
    {
        var raw = BuildRfc9421RawEcdsa(keyId, headers, method, path, created, expires);
        return new TokenInput(TokenKind.Rfc9421HttpSignature, raw, headers, method, path);
    }

    /// <summary>
    ///     Builds the packed RawValue (Signature-Input line + newline + Signature line)
    ///     for a valid Ed25519-signed RFC 9421 request.
    ///     Format mirrors <c>Rfc9421TestSigner</c> in the test project.
    /// </summary>
    private string BuildRfc9421Raw(
        string keyId, string alg,
        IReadOnlyDictionary<string, string> headers,
        string method, string path,
        long created, long? expires, byte[] privKey)
    {
        string[] components = ["@method", "@path", "host"];
        var paramsValue = BuildParamsValue(components, keyId, alg, created, expires);
        var sigBase = BuildSignatureBase(components, paramsValue, method, path, headers);
        var sig = SignEd25519(Encoding.UTF8.GetBytes(sigBase), privKey);
        return $"sig1={paramsValue}\nsig1=:{Convert.ToBase64String(sig)}:";
    }

    private string BuildRfc9421RawEcdsa(
        string keyId,
        IReadOnlyDictionary<string, string> headers,
        string method, string path,
        long created, long? expires)
    {
        string[] components = ["@method", "@path", "host"];
        var paramsValue = BuildParamsValue(components, keyId, "ecdsa-p256-sha256", created, expires);
        var sigBase = BuildSignatureBase(components, paramsValue, method, path, headers);
        var sig = _ecdsaSigner.SignData(
            Encoding.UTF8.GetBytes(sigBase),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"sig1={paramsValue}\nsig1=:{Convert.ToBase64String(sig)}:";
    }

    private static string BuildParamsValue(
        string[] components, string keyId, string alg, long created, long? expires)
    {
        var sb = new StringBuilder();
        sb.Append('(');
        sb.Append(string.Join(' ', components.Select(c => $"\"{c}\"")));
        sb.Append(')');
        sb.Append(";created=").Append(created);
        if (expires.HasValue) sb.Append(";expires=").Append(expires.Value);
        sb.Append(";keyid=\"").Append(keyId).Append('"');
        sb.Append(";alg=\"").Append(alg).Append('"');
        return sb.ToString();
    }

    private static string BuildSignatureBase(
        string[] components, string paramsValue,
        string method, string path,
        IReadOnlyDictionary<string, string> headers)
    {
        var sb = new StringBuilder();
        foreach (var c in components)
        {
            var val = c switch
            {
                "@method" => method.ToUpperInvariant(),
                "@path" => path,
                "host" => headers.TryGetValue("host", out var h) ? h.Trim() : "",
                _ => ""
            };
            sb.Append('"').Append(c).Append("\": ").Append(val).Append('\n');
        }

        sb.Append("\"@signature-params\": ").Append(paramsValue);
        return sb.ToString();
    }

    private static string TamperSignatureLine(string rawValue)
    {
        var nl = rawValue.IndexOf('\n');
        if (nl < 0) return rawValue;
        var line2 = rawValue[(nl + 1)..];
        var b64Start = line2.IndexOf(':') + 1;
        var b64End = line2.LastIndexOf(':');
        if (b64Start <= 0 || b64End <= b64Start) return rawValue;

        var sigBytes = Convert.FromBase64String(line2[b64Start..b64End]);
        sigBytes[0] ^= 0xFF; // flip first byte — guaranteed invalid
        return rawValue[..(nl + 1)] +
               line2[..b64Start] +
               Convert.ToBase64String(sigBytes) +
               line2[b64End..];
    }

    /// <summary>
    ///     Mints a canonical-JSON-signed bearer token (base64 of signed JSON).
    ///     Inlined from <c>SignedTokenTestMinter</c>; no test-project reference.
    /// </summary>
    private static string MintSignedToken(IReadOnlyDictionary<string, string> fields, byte[] privKey)
    {
        // 1. Canonical JSON (sorted, no "signature" field)
        using var cStream = new MemoryStream();
        using (var w = new Utf8JsonWriter(cStream, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            foreach (var kv in fields.Where(f => f.Key != "signature").OrderBy(f => f.Key, StringComparer.Ordinal))
                w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();
        }
        var canonical = Encoding.UTF8.GetString(cStream.ToArray());
        var sig = Convert.ToBase64String(SignEd25519(Encoding.UTF8.GetBytes(canonical), privKey));

        // 2. Full JSON with signature field
        using var jStream = new MemoryStream();
        using (var w = new Utf8JsonWriter(jStream, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            foreach (var kv in fields) w.WriteString(kv.Key, kv.Value);
            w.WriteString("signature", sig);
            w.WriteEndObject();
        }
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(jStream.ToArray())));
    }

    /// <summary>
    ///     Tampers only the "signature" field value inside the JSON so the outer
    ///     structure and UTF-8 remain valid. The verifier parses the full document,
    ///     reconstructs canonical content, and fails on <see cref="CryptoSignatureValidator.Verify"/>.
    ///     This exercises the "JsonDocument-heavy" path the spec calls out.
    /// </summary>
    private static string TamperSignatureField(string base64Token)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Token));
        const string marker = "\"signature\":\"";
        var start = json.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return base64Token; // no change if not found
        start += marker.Length;
        // Flip one base64 character in the signature value (stays printable ASCII)
        var chars = json.ToCharArray();
        chars[start] = chars[start] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered));
    }

    private static (byte[] PublicKey, byte[] PrivateKey) GenEd25519Pair()
    {
        var ed = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(ed, new KeyCreationParameters
            { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (key.PublicKey.Export(KeyBlobFormat.RawPublicKey), key.Export(KeyBlobFormat.RawPrivateKey));
    }

    private static byte[] SignEd25519(byte[] data, byte[] privKey)
    {
        var ed = SignatureAlgorithm.Ed25519;
        using var key = Key.Import(ed, privKey, KeyBlobFormat.RawPrivateKey);
        return ed.Sign(key, data);
    }
}

/// <summary>
///     Benchmarks for <see cref="PublicKeyRegistry.TryResolve"/> across varying key-set sizes.
///     The registry is lock-free (volatile array reference); the scan is O(n) linear.
///     Hit benchmarks look up the last key to exercise the worst-case full-scan path.
///     Miss benchmarks confirm that an absent keyid still scans both arrays completely.
///     Expect zero allocations at all sizes.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class WebBotAuthRegistryBenchmarks
{
    [Params(1, 50, 500)]
    public int RegistrySize;

    private PublicKeyRegistry _registry = null!;
    private string _hitKeyId = null!;
    private const string MissKeyId = "does-not-exist-xyz";

    [GlobalSetup]
    public void Setup()
    {
        var entries = new List<PublicKeyEntry>(RegistrySize);
        for (var i = 0; i < RegistrySize; i++)
        {
            var (pub, _) = GenEd25519Pair();
            entries.Add(new PublicKeyEntry($"key-{i}", $"Bot{i}", pub.AsMemory(), "ed25519", null, "bench"));
        }

        _registry = new PublicKeyRegistry();
        _registry.SeedManual(entries);
        _hitKeyId = $"key-{RegistrySize - 1}"; // last entry = worst-case linear scan
    }

    /// <summary>Worst-case hit: key found at the end of the manual array.</summary>
    [Benchmark]
    public bool TryResolve_Hit() => _registry.TryResolve(_hitKeyId, out _);

    /// <summary>Full miss: scans both manual and fetched arrays, finds nothing.</summary>
    [Benchmark]
    public bool TryResolve_Miss() => _registry.TryResolve(MissKeyId, out _);

    private static (byte[] PublicKey, byte[] PrivateKey) GenEd25519Pair()
    {
        var ed = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(ed, new KeyCreationParameters
            { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (key.PublicKey.Export(KeyBlobFormat.RawPublicKey), key.Export(KeyBlobFormat.RawPrivateKey));
    }
}