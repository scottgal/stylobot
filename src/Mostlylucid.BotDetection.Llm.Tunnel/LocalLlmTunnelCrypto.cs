using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mostlylucid.BotDetection.Llm.Tunnel;

/// <summary>
/// Cryptographic helpers for the local LLM tunnel:
/// HMAC-SHA256 request signing, nonce replay protection,
/// and optional ECDH/HKDF/AES-GCM payload envelope encryption.
/// </summary>
public sealed class LocalLlmTunnelCrypto : IDisposable
{
    // Nonces expire after this duration BEYOND the request's ExpiresAt.
    // Requests have a 30s window; this buffer covers clock skew and
    // ensures nonces are retained long enough to detect replays across the full window.
    private static readonly TimeSpan NonceTtl = TimeSpan.FromSeconds(60);

    // Internal cache: nonce -> expiry
    private readonly ConcurrentDictionary<string, DateTime> _nonceCache = new();
    private readonly Timer _cleanupTimer;

    public LocalLlmTunnelCrypto()
    {
        // Periodically evict expired nonces
        _cleanupTimer = new Timer(_ => EvictExpiredNonces(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // ── Signing ────────────────────────────────────────────────────────────

    /// <summary>
    /// Compute the canonical message to sign for an inference request.
    /// All fields that must be integrity-protected are included.
    /// </summary>
    private static string BuildCanonicalRequest(LlmSignedInferenceRequest req)
    {
        // Canonical form: fixed set of fields joined with \n, payload as compact JSON
        var payloadJson = JsonSerializer.Serialize(req.Payload, TunnelJsonContext.Default.LlmTunnelCompletionRequest);
        return string.Join("\n",
            req.RequestId,
            req.NodeId,
            req.KeyId,
            req.Nonce,
            req.IssuedAt.ToString("O"),
            req.ExpiresAt.ToString("O"),
            payloadJson);
    }

    /// <summary>Sign a request using HMAC-SHA256 with the shared secret.</summary>
    public string SignRequest(LlmSignedInferenceRequest req, byte[] sharedSecret)
    {
        var canonical = BuildCanonicalRequest(req);
        var mac = HMACSHA256.HashData(sharedSecret, Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>Verify a request signature. Returns false on bad signature.</summary>
    public bool VerifyRequest(LlmSignedInferenceRequest req, byte[] sharedSecret)
    {
        var canonical = BuildCanonicalRequest(req);
        var expected = HMACSHA256.HashData(sharedSecret, Encoding.UTF8.GetBytes(canonical));
        var providedBytes = FromBase64Url(req.Signature);
        return CryptographicOperations.FixedTimeEquals(expected, providedBytes);
    }

    // ── Response signing ───────────────────────────────────────────────────

    private static string BuildCanonicalResponse(LlmSignedInferenceResponse res)
    {
        return string.Join("\n",
            res.RequestId,
            res.Model,
            res.Content,
            res.LatencyMs.ToString());
    }

    public string SignResponse(LlmSignedInferenceResponse res, byte[] sharedSecret)
    {
        var canonical = BuildCanonicalResponse(res);
        var mac = HMACSHA256.HashData(sharedSecret, Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public bool VerifyResponse(LlmSignedInferenceResponse res, byte[] sharedSecret)
    {
        var canonical = BuildCanonicalResponse(res);
        var expected = HMACSHA256.HashData(sharedSecret, Encoding.UTF8.GetBytes(canonical));
        var providedBytes = FromBase64Url(res.Signature);
        return CryptographicOperations.FixedTimeEquals(expected, providedBytes);
    }

    // ── Validation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Validate that a request has not expired.
    /// Returns false when the request is expired.
    /// </summary>
    public static bool IsRequestExpired(LlmSignedInferenceRequest req)
        => DateTime.UtcNow > req.ExpiresAt;

    /// <summary>
    /// Validate node and key binding. Returns false when either id mismatches.
    /// </summary>
    public static bool ValidateBinding(LlmSignedInferenceRequest req,
        string expectedNodeId, string expectedKeyId)
        => req.NodeId == expectedNodeId && req.KeyId == expectedKeyId;

    // ── Nonce replay protection ────────────────────────────────────────────

    /// <summary>
    /// Returns true if the nonce has not been seen before (not a replay),
    /// and records it for future checks. Returns false on replay.
    /// </summary>
    public bool TryConsumeNonce(string nonce, DateTime expiresAt)
    {
        var cacheUntil = expiresAt + NonceTtl; // keep a bit longer than request TTL
        return _nonceCache.TryAdd(nonce, cacheUntil);
    }

    private void EvictExpiredNonces()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, expiry) in _nonceCache)
        {
            if (now > expiry) _nonceCache.TryRemove(key, out _);
        }
    }

    // ── Key generation helpers ─────────────────────────────────────────────

    /// <summary>Generate a new HMAC-SHA256 signing secret (32 random bytes).</summary>
    public static byte[] GenerateSigningSecret()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    /// <summary>Encode bytes as base64url (no padding).</summary>
    public static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Decode a base64url string (with or without padding).</summary>
    public static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        var rem = padded.Length % 4;
        if (rem != 0) padded += new string('=', 4 - rem);
        return Convert.FromBase64String(padded);
    }

    // ── Optional ECDH/HKDF/AES-GCM envelope encryption ────────────────────

    /// <summary>
    /// Derive a 256-bit session key from two ECDH public/private key pairs
    /// using HKDF-SHA256. Both sides must call this with their own private key
    /// and the other party's public key.
    /// </summary>
    public static byte[] DeriveSessionKey(ECDiffieHellman localPrivateKey,
        ECDiffieHellmanPublicKey remotePublicKey, byte[]? salt = null)
    {
        // ECDH raw shared secret
        var sharedSecret = localPrivateKey.DeriveKeyMaterial(remotePublicKey);
        // HKDF-SHA256 with "stylobot-llm-tunnel-v1" info label
        var info = Encoding.UTF8.GetBytes("stylobot-llm-tunnel-v1");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, info);
    }

    /// <summary>
    /// Encrypt a plaintext payload with AES-256-GCM.
    /// Returns an envelope with nonce, ciphertext, and tag (all base64url).
    /// </summary>
    public static LlmTunnelEnvelope Seal(byte[] plaintext, byte[] sessionKey)
    {
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes

        using var aes = new AesGcm(sessionKey, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return new LlmTunnelEnvelope
        {
            Encrypted = true,
            EncNonce = ToBase64Url(nonce),
            Ciphertext = ToBase64Url(ciphertext),
            Tag = ToBase64Url(tag)
        };
    }

    /// <summary>
    /// Decrypt an AES-256-GCM envelope. Throws CryptographicException on
    /// authentication failure (tampered ciphertext or wrong key).
    /// </summary>
    public static byte[] Open(LlmTunnelEnvelope envelope, byte[] sessionKey)
    {
        if (!envelope.Encrypted)
            throw new InvalidOperationException("Envelope is not encrypted.");
        if (envelope.EncNonce is null || envelope.Ciphertext is null || envelope.Tag is null)
            throw new FormatException("Encrypted envelope missing nonce, ciphertext, or tag.");

        var nonce = FromBase64Url(envelope.EncNonce);
        var ciphertext = FromBase64Url(envelope.Ciphertext);
        var tag = FromBase64Url(envelope.Tag);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(sessionKey, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public void Dispose() => _cleanupTimer.Dispose();
}
