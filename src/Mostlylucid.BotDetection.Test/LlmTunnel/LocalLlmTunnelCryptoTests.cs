using System.Security.Cryptography;
using Mostlylucid.BotDetection.Llm.Tunnel;
using Xunit;

namespace Mostlylucid.BotDetection.Test.LlmTunnel;

public class LocalLlmTunnelCryptoTests : IDisposable
{
    private readonly LocalLlmTunnelCrypto _crypto = new();
    private readonly byte[] _secret = LocalLlmTunnelCrypto.GenerateSigningSecret();

    private static LlmSignedInferenceRequest MakeRequest(string nonce = "abc123")
        => new()
        {
            RequestId = "llmreq_01hw",
            TenantId = "tenant_01",
            NodeId = "llmn_01hw",
            KeyId = "k_01hw",
            Nonce = nonce,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddSeconds(30),
            Payload = new LlmTunnelCompletionRequest
            {
                Model = "llama3.2:3b",
                Messages = [new LlmTunnelMessage { Role = "user", Content = "classify" }]
            },
            Signature = ""
        };

    [Fact]
    public void SignAndVerify_ValidRequest_ReturnsTrue()
    {
        var req = MakeRequest();
        req.Signature = _crypto.SignRequest(req, _secret);
        Assert.True(_crypto.VerifyRequest(req, _secret));
    }

    [Fact]
    public void VerifyRequest_WrongKey_ReturnsFalse()
    {
        var req = MakeRequest();
        req.Signature = _crypto.SignRequest(req, _secret);
        var wrongKey = LocalLlmTunnelCrypto.GenerateSigningSecret();
        Assert.False(_crypto.VerifyRequest(req, wrongKey));
    }

    [Fact]
    public void VerifyRequest_TamperedPayload_ReturnsFalse()
    {
        var req = MakeRequest();
        req.Signature = _crypto.SignRequest(req, _secret);
        req.Payload.Model = "different-model";
        Assert.False(_crypto.VerifyRequest(req, _secret));
    }

    [Fact]
    public void IsRequestExpired_FutureExpiry_ReturnsFalse()
    {
        var req = MakeRequest();
        req.ExpiresAt = DateTime.UtcNow.AddSeconds(30);
        Assert.False(LocalLlmTunnelCrypto.IsRequestExpired(req));
    }

    [Fact]
    public void IsRequestExpired_PastExpiry_ReturnsTrue()
    {
        var req = MakeRequest();
        req.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        Assert.True(LocalLlmTunnelCrypto.IsRequestExpired(req));
    }

    [Fact]
    public void ValidateBinding_CorrectIds_ReturnsTrue()
    {
        var req = MakeRequest();
        Assert.True(LocalLlmTunnelCrypto.ValidateBinding(req, "llmn_01hw", "k_01hw"));
    }

    [Fact]
    public void ValidateBinding_WrongNodeId_ReturnsFalse()
    {
        var req = MakeRequest();
        Assert.False(LocalLlmTunnelCrypto.ValidateBinding(req, "wrong_node", "k_01hw"));
    }

    [Fact]
    public void TryConsumeNonce_FirstUse_ReturnsTrue()
    {
        var expiry = DateTime.UtcNow.AddSeconds(30);
        Assert.True(_crypto.TryConsumeNonce("unique-nonce-1", expiry));
    }

    [Fact]
    public void TryConsumeNonce_ReplayedNonce_ReturnsFalse()
    {
        var expiry = DateTime.UtcNow.AddSeconds(30);
        _crypto.TryConsumeNonce("replay-nonce", expiry);
        Assert.False(_crypto.TryConsumeNonce("replay-nonce", expiry));
    }

    [Fact]
    public void SealAndOpen_RoundTrip_ReturnsOriginalPlaintext()
    {
        var sessionKey = new byte[32];
        RandomNumberGenerator.Fill(sessionKey);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("hello tunnel");

        var envelope = LocalLlmTunnelCrypto.Seal(plaintext, sessionKey);
        var decrypted = LocalLlmTunnelCrypto.Open(envelope, sessionKey);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Open_WrongKey_ThrowsCryptographicException()
    {
        var sessionKey = new byte[32];
        RandomNumberGenerator.Fill(sessionKey);
        var wrongKey = new byte[32];
        RandomNumberGenerator.Fill(wrongKey);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("secret data");

        var envelope = LocalLlmTunnelCrypto.Seal(plaintext, sessionKey);

        Assert.ThrowsAny<CryptographicException>(() => LocalLlmTunnelCrypto.Open(envelope, wrongKey));
    }

    [Fact]
    public void SignResponseAndVerify_ValidResponse_ReturnsTrue()
    {
        var res = new LlmSignedInferenceResponse
        {
            RequestId = "llmreq_01hw",
            Model = "llama3.2:3b",
            Content = "{ \"isBot\": true }",
            LatencyMs = 420,
            Signature = ""
        };
        res.Signature = _crypto.SignResponse(res, _secret);
        Assert.True(_crypto.VerifyResponse(res, _secret));
    }

    public void Dispose() => _crypto.Dispose();
}
