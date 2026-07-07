using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Verifies StyloFlow license capability tokens carried in
///     <c>Authorization: License &lt;token&gt;</c>, where <c>&lt;token&gt;</c> is
///     base64 of the signed license JSON. Verifies the Ed25519 signature against
///     the configured trust anchors (canonical content = sorted keys, signature
///     excluded — matching <c>LicenseSigningService</c>), then checks expiry.
///     Stateless (Molecule).
/// </summary>
internal sealed class CapabilityTokenVerifier : ITokenKindVerifier
{
    private readonly ISignatureValidator _crypto;
    private readonly IOptions<TokenVerifierOptions> _options;
    private readonly TimeProvider _time;

    public CapabilityTokenVerifier(
        ISignatureValidator crypto,
        IOptions<TokenVerifierOptions> options,
        TimeProvider? timeProvider = null)
    {
        _crypto = crypto;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    public TokenKind Kind => TokenKind.LicenseCapability;

    public TokenVerdict Verify(TokenInput input)
    {
        var started = _time.GetTimestamp();
        var opts = _options.Value;

        TokenVerdict Verdict(TokenOutcome outcome, string? subject = null,
            IReadOnlyDictionary<string, string>? claims = null)
            => new(outcome, null, subject, claims, _time.GetElapsedTime(started));

        // 1. base64 → license JSON.
        byte[] jsonBytes;
        try { jsonBytes = Convert.FromBase64String(input.RawValue.Trim()); }
        catch (FormatException) { return Verdict(TokenOutcome.Malformed); }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonBytes); }
        catch (JsonException) { return Verdict(TokenOutcome.Malformed); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Verdict(TokenOutcome.Malformed);

            // 2. The token must carry a base64 signature field.
            if (!doc.RootElement.TryGetProperty("signature", out var sigProp) ||
                sigProp.ValueKind != JsonValueKind.String)
                return Verdict(TokenOutcome.Malformed);
            var sigB64 = sigProp.GetString();
            if (string.IsNullOrEmpty(sigB64)) return Verdict(TokenOutcome.Malformed);
            byte[] signature;
            try { signature = Convert.FromBase64String(sigB64); }
            catch (FormatException) { return Verdict(TokenOutcome.Malformed); }

            var claims = ExtractClaims(doc.RootElement);
            var subject = claims.TryGetValue("issuedTo", out var issuedTo) ? issuedTo : null;

            // 3. No configured issuer keys → nothing can be trusted.
            if (opts.CapabilityTrustAnchors.Count == 0)
                return Verdict(TokenOutcome.MissingKey, subject, claims);

            // 4. Verify against each anchor; first match wins.
            var data = Encoding.UTF8.GetBytes(CanonicalContent(doc.RootElement));
            string? anchorName = null;
            var verified = false;
            foreach (var anchor in opts.CapabilityTrustAnchors)
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
    ///     Canonical signable content: sorted keys, signature field excluded,
    ///     non-indented. Byte-identical to <c>LicenseSigningService.GetSignableContent</c>.
    /// </summary>
    private static string CanonicalContent(JsonElement root)
    {
        var sorted = root.EnumerateObject()
            .Where(p => p.Name != "signature")
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

    private static Dictionary<string, string> ExtractClaims(JsonElement root)
    {
        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "signature") continue;
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
        if (!root.TryGetProperty("expiry", out var expiryProp)) return false;

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
