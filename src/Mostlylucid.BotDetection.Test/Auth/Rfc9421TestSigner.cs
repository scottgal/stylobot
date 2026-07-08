using System.Text;

namespace Mostlylucid.BotDetection.Test.Auth;

/// <summary>
///     Builds RFC 9421 HTTP Message Signatures for the verifier tests, and
///     documents the exact <c>TokenInput.RawValue</c> wire format the wba-atom
///     agent must produce:
///     <code>
///     &lt;Signature-Input header value&gt;\n&lt;Signature header value&gt;
///     </code>
///     i.e. line 1 is the value of the <c>Signature-Input</c> header
///     (<c>sig1=("@method" …);created=…;keyid="…";alg="…"</c>) and line 2 is the
///     value of the <c>Signature</c> header (<c>sig1=:base64:</c>).
/// </summary>
internal sealed class Rfc9421TestSigner
{
    public string Label { get; init; } = "sig1";
    public required IReadOnlyList<string> Components { get; init; }

    /// <summary>Resolved value for each covered component (both derived like "@method" and headers).</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    public long? Created { get; init; }
    public long? Expires { get; init; }
    public required string KeyId { get; init; }
    public string? Algorithm { get; init; }
    public string? Nonce { get; init; }
    public string? Tag { get; init; }

    /// <summary>The exact string that follows <c>label=</c> in Signature-Input and is the @signature-params value.</summary>
    public string ParamsValue()
    {
        var sb = new StringBuilder();
        sb.Append('(')
          .Append(string.Join(' ', Components.Select(c => $"\"{c}\"")))
          .Append(')');
        if (Created.HasValue) sb.Append(";created=").Append(Created.Value);
        if (Expires.HasValue) sb.Append(";expires=").Append(Expires.Value);
        sb.Append(";keyid=\"").Append(KeyId).Append('"');
        if (Algorithm is not null) sb.Append(";alg=\"").Append(Algorithm).Append('"');
        if (Nonce is not null) sb.Append(";nonce=\"").Append(Nonce).Append('"');
        if (Tag is not null) sb.Append(";tag=\"").Append(Tag).Append('"');
        return sb.ToString();
    }

    public string SignatureBase()
    {
        var sb = new StringBuilder();
        foreach (var c in Components)
            sb.Append('"').Append(c).Append("\": ").Append(Values[c]).Append('\n');
        sb.Append("\"@signature-params\": ").Append(ParamsValue());
        return sb.ToString();
    }

    /// <summary>Signs with Ed25519 and returns the packed <c>RawValue</c>.</summary>
    public string BuildEd25519(byte[] rawPrivateKey)
    {
        var sig = CryptoTestHelpers.SignEd25519(Encoding.UTF8.GetBytes(SignatureBase()), rawPrivateKey);
        return Pack(Convert.ToBase64String(sig));
    }

    /// <summary>Packs an already-computed base64 signature into the <c>RawValue</c> shape.</summary>
    public string Pack(string signatureBase64)
    {
        var signatureInput = $"{Label}={ParamsValue()}";
        var signature = $"{Label}=:{signatureBase64}:";
        return signatureInput + "\n" + signature;
    }
}
