namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Verifies cryptographic request credentials — RFC 9421 HTTP Message
///     Signatures (Web Bot Auth) and StyloFlow license capability tokens — behind
///     one abstraction. Both are <c>sig + claims + expiry</c> shapes; one
///     interface over both prevents parallel crypto pipelines.
///     <para>
///         Contract C2 from <c>docs/superpowers/specs/2026-07-07-agent-native-web-shape.md</c>.
///         Locked — do not re-invent. Taxonomy: the implementations are Molecules
///         (stateless, pure functions of their input — no per-request state, no IO).
///     </para>
/// </summary>
public interface ITokenVerifier
{
    TokenVerdict Verify(TokenInput input);
}

/// <summary>Everything the verifier needs about one credential on one request.</summary>
public readonly record struct TokenInput(
    TokenKind Kind,               // Rfc9421HttpSignature | LicenseCapability
    string RawValue,              // full signature-input+signature block for 9421; base64 blob for capability
    IReadOnlyDictionary<string, string> CoveredHeaders,   // request headers covered by the signature (9421 only)
    string RequestMethod,
    string RequestPath);

/// <summary>The verdict for one credential. <see cref="Elapsed"/> is measured internally so a caller can't fake it.</summary>
public sealed record TokenVerdict(
    TokenOutcome Outcome,         // Valid | InvalidSignature | Expired | UnknownKey | Malformed | MissingKey
    string? KeyId,                // populated when at least the header parsed
    string? SubjectName,          // AgentName for 9421 (from PublicKeyRegistry); IssuedTo for capability
    IReadOnlyDictionary<string, string>? Claims,   // capability claims OR the 9421 signature parameters
    TimeSpan Elapsed);            // verify-time — feeds the dashboard latency panel

/// <summary>Which kind of credential a <see cref="TokenInput"/> carries.</summary>
public enum TokenKind { Rfc9421HttpSignature, LicenseCapability }

/// <summary>The outcome axis of a <see cref="TokenVerdict"/> — the locked error taxonomy.</summary>
public enum TokenOutcome { Valid, InvalidSignature, Expired, UnknownKey, Malformed, MissingKey }