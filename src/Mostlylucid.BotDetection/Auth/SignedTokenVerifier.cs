using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Verifies a generic signed bearer token — base64 of a JSON document whose
///     signature lives in a configured field (default <c>signature</c>) over the
///     canonical content (sorted keys, signature field excluded, non-indented).
///     Verifies the Ed25519/ECDSA signature against the configured trust anchors,
///     then checks an optional expiry claim, and surfaces every claim raw.
///     Stateless (Molecule).
///     <para>
///         FOSS carries no knowledge of what the token means — a consumer that
///         treats it as a license, an entitlement, or anything else interprets the
///         surfaced <see cref="TokenVerdict.Claims"/> itself. The subject / expiry
///         claim names and the signature field are configurable so any convention
///         can be verified.
///     </para>
/// </summary>
internal sealed class SignedTokenVerifier : ITokenKindVerifier
{
    private readonly ISignatureValidator _crypto;
    private readonly IOptions<TokenVerifierOptions> _options;
    private readonly TimeProvider _time;

    public SignedTokenVerifier(
        ISignatureValidator crypto,
        IOptions<TokenVerifierOptions> options,
        TimeProvider? timeProvider = null)
    {
        _crypto = crypto;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    public TokenKind Kind => TokenKind.SignedBearerToken;

    public TokenVerdict Verify(TokenInput input)
    {
        var started = _time.GetTimestamp();
        var opts = _options.Value;
        var signatureField = string.IsNullOrWhiteSpace(opts.SignedTokenSignatureField)
            ? "signature"
            : opts.SignedTokenSignatureField;

        TokenVerdict Verdict(TokenOutcome outcome, string? subject = null,
            IReadOnlyDictionary<string, string>? claims = null)
            => new(outcome, null, subject, claims, _time.GetElapsedTime(started));

        // 1. base64 → JSON.
        byte[] jsonBytes;
        try { jsonBytes = Convert.FromBase64String(input.RawValue.Trim()); }
        catch (FormatException) { return Verdict(TokenOutcome.Malformed); }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonBytes); }
        catch (JsonException) { return Verdict(TokenOutcome.Malformed); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Verdict(TokenOutcome.Malformed);

            // 2. The token must carry a base64 signature in the configured field.
            if (!doc.RootElement.TryGetProperty(signatureField, out var sigProp) ||
                sigProp.ValueKind != JsonValueKind.String)
                return Verdict(TokenOutcome.Malformed);
            var sigB64 = sigProp.GetString();
            if (string.IsNullOrEmpty(sigB64)) return Verdict(TokenOutcome.Malformed);
            byte[] signature;
            try { signature = Convert.FromBase64String(sigB64); }
            catch (FormatException) { return Verdict(TokenOutcome.Malformed); }

            var claims = ExtractClaims(doc.RootElement, signatureField);
            var subject = claims.TryGetValue(opts.SignedTokenSubjectClaim, out var s) ? s : null;

            // 3. No configured issuer keys → nothing can be trusted.
            if (opts.TrustAnchors.Count == 0)
                return Verdict(TokenOutcome.MissingKey, subject, claims);

            // 4. Verify against each anchor; first match wins.
            var data = Encoding.UTF8.GetBytes(CanonicalContent(doc.RootElement, signatureField));
            string? anchorName = null;
            var verified = false;
            foreach (var anchor in opts.TrustAnchors)
            {
                byte[] pub;
                try { pub = Convert.FromBase64String(anchor.PublicKey); }
                catch (FormatException) { continue; }

                if (_crypto.Verify(anchor.Algorithm, data, signature, pub))
                {
                    verified = true;
                    anchorName = anchor.Name;
                    break;
                }
            }

            if (!verified) return Verdict(TokenOutcome.InvalidSignature, subject, claims);

            subject ??= anchorName;

            // 5. Freshness — checked only after the signature is trusted.
            if (IsExpired(doc.RootElement, opts))
                return Verdict(TokenOutcome.Expired, subject, claims);

            return Verdict(TokenOutcome.Valid, subject, claims);
        }
    }

    /// <summary>
    ///     Canonical signable content: sorted keys, the signature field excluded,
    ///     non-indented. A signer produces the same bytes to sign; the verifier
    ///     rebuilds them to check.
    /// </summary>
    private static string CanonicalContent(JsonElement root, string signatureField)
    {
        var sorted = root.EnumerateObject()
            .Where(p => p.Name != signatureField)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var prop in sorted) prop.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Dictionary<string, string> ExtractClaims(JsonElement root, string signatureField)
    {
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == signatureField) continue;
            claims[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => prop.Value.GetRawText()
            };
        }

        return claims;
    }

    private bool IsExpired(JsonElement root, TokenVerifierOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.SignedTokenExpiryClaim)) return false;
        if (!root.TryGetProperty(opts.SignedTokenExpiryClaim, out var expiryProp)) return false;

        DateTimeOffset expiresAt;
        switch (expiryProp.ValueKind)
        {
            case JsonValueKind.String when DateTimeOffset.TryParse(
                expiryProp.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed):
                expiresAt = parsed;
                break;
            case JsonValueKind.Number when expiryProp.TryGetInt64(out var unix):
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
                break;
            default:
                return false;
        }

        return _time.GetUtcNow() > expiresAt + opts.MaxClockSkew;
    }
}
