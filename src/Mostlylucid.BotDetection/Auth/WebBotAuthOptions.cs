namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Options for <see cref="Mostlylucid.BotDetection.Orchestration.Atoms.WebBotAuthApprovalAtom"/>.
///     Bound from config section <c>BotDetection:WebBotAuth</c>.
/// </summary>
public sealed class WebBotAuthOptions
{
    /// <summary>
    ///     Reserved for future defense-in-depth: re-verify the cached verdict
    ///     every N requests rather than trusting it for the full session
    ///     window. <c>null</c> (default) means "trust for the whole window".
    ///     Wiring this up is intentionally deferred — the slot is here so the
    ///     knob can be added without a schema change.
    /// </summary>
    public int? ReverifyEveryNRequests { get; set; } = null;
}

/// <summary>
///     Per-session cached result of a Web Bot Auth (RFC 9421 HTTP Message
///     Signature) verification. Stored on <see cref="Mostlylucid.BotDetection.Orchestration.Sessions.SessionAggregate"/>
///     so the atom only calls <see cref="ITokenVerifier"/> once per session window.
/// </summary>
/// <remarks>
///     <para>
///         Security invariant: this record carries ONLY public metadata. No
///         raw signature bytes, no signature base string, no key material.
///         The <see cref="KeyId"/> is the public identifier; <see cref="Verdict"/>
///         is the outcome enum; <see cref="SubjectName"/> is the agent's
///         display name from the public key registry.
///     </para>
/// </remarks>
/// <param name="KeyId">Public key identifier — no raw key material.</param>
/// <param name="Verdict">Outcome of the most recent verification.</param>
/// <param name="SubjectName">Agent name from the public key registry, if resolved.</param>
/// <param name="Algorithm">Algorithm string from the signature parameters, if present.</param>
public sealed record WebBotAuthCachedVerdict(
    string KeyId,
    TokenOutcome Verdict,
    string? SubjectName,
    string? Algorithm);