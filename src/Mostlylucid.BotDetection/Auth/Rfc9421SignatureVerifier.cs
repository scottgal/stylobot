using System.Text;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.WebBotAuth;

namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Verifies RFC 9421 HTTP Message Signatures (Web Bot Auth). Reconstructs the
///     signature base from the covered components, resolves the signing key via
///     <see cref="IPublicKeyRegistry"/>, and verifies with
///     <see cref="ISignatureValidator"/>. Stateless (Molecule).
///     <para>
///         Scope: bare covered components (no per-component parameters beyond the
///         common case), a single signature label, and the derived components
///         <c>@method</c>/<c>@path</c>/<c>@authority</c> computed natively — any
///         other derived component must be supplied by the atom in
///         <see cref="TokenInput.CoveredHeaders"/> under its <c>@name</c> key.
///         The signature <b>base</b> is verified before any freshness (created /
///         expires) check, so <see cref="TokenOutcome.InvalidSignature"/> takes
///         precedence over <see cref="TokenOutcome.Expired"/>.
///     </para>
/// </summary>
internal sealed class Rfc9421SignatureVerifier : ITokenKindVerifier
{
    private readonly IPublicKeyRegistry _registry;
    private readonly ISignatureValidator _crypto;
    private readonly IOptions<TokenVerifierOptions> _options;
    private readonly TimeProvider _time;

    public Rfc9421SignatureVerifier(
        IPublicKeyRegistry registry,
        ISignatureValidator crypto,
        IOptions<TokenVerifierOptions> options,
        TimeProvider? timeProvider = null)
    {
        _registry = registry;
        _crypto = crypto;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    public TokenKind Kind => TokenKind.Rfc9421HttpSignature;

    public TokenVerdict Verify(TokenInput input)
    {
        var started = _time.GetTimestamp();
        var opts = _options.Value;

        TokenVerdict Verdict(TokenOutcome outcome, string? keyId = null, string? subject = null,
            IReadOnlyDictionary<string, string>? claims = null)
            => new(outcome, keyId, subject, claims, _time.GetElapsedTime(started));

        // 1. Split the packed value into the Signature-Input and Signature lines.
        var lines = input.RawValue.Split('\n');
        if (lines.Length < 2) return Verdict(TokenOutcome.Malformed);
        var inputLine = lines[0].Trim('\r', ' ');
        var signatureLine = lines[1].Trim('\r', ' ');

        // 2. Parse Signature-Input: label=(<components>);<params>
        var inputEq = inputLine.IndexOf('=');
        if (inputEq <= 0) return Verdict(TokenOutcome.Malformed);
        var label = inputLine[..inputEq];
        var verbatimParams = inputLine[(inputEq + 1)..]; // used verbatim for @signature-params

        if (verbatimParams.Length == 0 || verbatimParams[0] != '(') return Verdict(TokenOutcome.Malformed);
        var close = verbatimParams.IndexOf(')');
        if (close < 0) return Verdict(TokenOutcome.Malformed);

        var components = ParseComponents(verbatimParams[1..close]);
        if (components.Count == 0) return Verdict(TokenOutcome.Malformed);
        var sigParams = ParseParams(verbatimParams[(close + 1)..]);

        // 3. Parse Signature: label=:base64:
        var sigEq = signatureLine.IndexOf('=');
        if (sigEq <= 0) return Verdict(TokenOutcome.Malformed);
        if (!string.Equals(signatureLine[..sigEq], label, StringComparison.Ordinal))
            return Verdict(TokenOutcome.Malformed);
        var sigWrapped = signatureLine[(sigEq + 1)..];
        if (sigWrapped.Length < 2 || sigWrapped[0] != ':' || sigWrapped[^1] != ':')
            return Verdict(TokenOutcome.Malformed);
        byte[] signature;
        try { signature = Convert.FromBase64String(sigWrapped[1..^1]); }
        catch (FormatException) { return Verdict(TokenOutcome.Malformed); }

        // 4. keyid — no key named means we cannot resolve anything.
        if (!sigParams.TryGetValue("keyid", out var keyId) || string.IsNullOrWhiteSpace(keyId))
            return Verdict(TokenOutcome.MissingKey);

        // 5. Resolve the key (rotation: a past NotAfter is no longer a known key).
        if (!_registry.TryResolve(keyId, out var key))
            return Verdict(TokenOutcome.UnknownKey, keyId);
        var now = _time.GetUtcNow();
        if (key.NotAfter is { } notAfter && now > notAfter + opts.MaxClockSkew)
            return Verdict(TokenOutcome.UnknownKey, keyId);

        // 6. Algorithm: explicit alg param wins, else the key's algorithm.
        var algorithm = sigParams.TryGetValue("alg", out var alg) && !string.IsNullOrWhiteSpace(alg)
            ? alg
            : key.Algorithm;
        if (opts.AllowedAlgorithms.Count > 0 &&
            !opts.AllowedAlgorithms.Any(a => string.Equals(a, algorithm, StringComparison.OrdinalIgnoreCase)))
            return Verdict(TokenOutcome.Malformed, keyId);
        if (!_crypto.Supports(algorithm))
            return Verdict(TokenOutcome.Malformed, keyId);

        // 7. RequireCreated policy.
        var hasCreated = sigParams.ContainsKey("created");
        if (opts.RequireCreated && !hasCreated)
            return Verdict(TokenOutcome.Malformed, keyId);

        // 8. Reconstruct the signature base.
        if (!TryBuildSignatureBase(components, verbatimParams, input, out var signatureBase))
            return Verdict(TokenOutcome.Malformed, keyId);

        // 9. Verify the signature over the base BEFORE trusting any timestamps.
        if (!_crypto.Verify(algorithm, Encoding.UTF8.GetBytes(signatureBase), signature, key.PublicKey.Span))
            return Verdict(TokenOutcome.InvalidSignature, keyId, key.AgentName);

        // 10. Freshness — created/expires are integrity-protected, checked post-verify.
        if (IsExpired(sigParams, now, opts))
            return Verdict(TokenOutcome.Expired, keyId, key.AgentName, ToClaims(sigParams));

        return Verdict(TokenOutcome.Valid, keyId, key.AgentName, ToClaims(sigParams));
    }

    private bool TryBuildSignatureBase(
        IReadOnlyList<string> components, string verbatimParams, TokenInput input, out string signatureBase)
    {
        signatureBase = "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in input.CoveredHeaders) headers[kv.Key] = kv.Value;

        var sb = new StringBuilder();
        foreach (var component in components)
        {
            if (!TryResolveComponent(component, input, headers, out var value))
                return false;
            sb.Append('"').Append(component).Append("\": ").Append(value).Append('\n');
        }

        sb.Append("\"@signature-params\": ").Append(verbatimParams);
        signatureBase = sb.ToString();
        return true;
    }

    private static bool TryResolveComponent(
        string component, TokenInput input, IReadOnlyDictionary<string, string> headers, out string value)
    {
        value = "";
        if (component.StartsWith('@'))
        {
            switch (component)
            {
                case "@method":
                    value = input.RequestMethod.ToUpperInvariant();
                    return true;
                case "@path":
                    value = input.RequestPath;
                    return true;
                case "@authority":
                    if (headers.TryGetValue("@authority", out var authority) ||
                        headers.TryGetValue("host", out authority))
                    {
                        value = authority.Trim();
                        return true;
                    }
                    return false;
                default:
                    // Any other derived component must be supplied by the atom
                    // (which has the full request) under its "@name" key.
                    if (headers.TryGetValue(component, out var derived))
                    {
                        value = derived.Trim();
                        return true;
                    }
                    return false;
            }
        }

        if (headers.TryGetValue(component, out var header))
        {
            value = header.Trim();
            return true;
        }

        return false;
    }

    private bool IsExpired(IReadOnlyDictionary<string, string> sigParams, DateTimeOffset now, TokenVerifierOptions opts)
    {
        if (sigParams.TryGetValue("expires", out var expiresRaw) && long.TryParse(expiresRaw, out var expires))
        {
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires);
            if (now > expiresAt + opts.MaxClockSkew) return true;
        }

        if (opts.MaxSignatureAge is { } maxAge &&
            sigParams.TryGetValue("created", out var createdRaw) && long.TryParse(createdRaw, out var created))
        {
            var createdAt = DateTimeOffset.FromUnixTimeSeconds(created);
            if (now - createdAt > maxAge + opts.MaxClockSkew) return true;
        }

        return false;
    }

    private static IReadOnlyList<string> ParseComponents(string inside)
    {
        var result = new List<string>();
        foreach (var token in inside.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            result.Add(token.Trim('"'));
        return result;
    }

    private static Dictionary<string, string> ParseParams(string segment)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in segment.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            result[key] = value;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ToClaims(IReadOnlyDictionary<string, string> sigParams)
        => new Dictionary<string, string>(sigParams, StringComparer.Ordinal);
}
