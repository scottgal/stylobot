namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Configuration surface for <see cref="ITokenVerifier"/> (contract C7).
///     Holds every knob for both token kinds — RFC 9421 HTTP signatures and
///     StyloFlow license capability tokens. Bound from
///     <c>BotDetection:TokenVerifier</c>.
///     <para>
///         Capability trust anchors live here (not on the caps-atom's
///         <c>CapabilityTokenOptions</c>) so all <i>verification</i> config has a
///         single home owned by the token-verifier. The caps-atom's options own
///         <i>policy</i> (default LogOnly mode, claim→action maps), not trust.
///     </para>
/// </summary>
public sealed class TokenVerifierOptions
{
    /// <summary>Configuration section name when binding from <c>IConfiguration</c>.</summary>
    public const string SectionName = "BotDetection:TokenVerifier";

    // ── RFC 9421 HTTP Message Signatures ────────────────────────────────────

    /// <summary>
    ///     Clock-skew tolerance applied to the signature's <c>created</c> /
    ///     <c>expires</c> parameters. Default 5 minutes.
    /// </summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     When set, a signature whose <c>created</c> is older than this is treated
    ///     as expired even if it carries no <c>expires</c> parameter (a replay
    ///     window). Null = no age ceiling beyond an explicit <c>expires</c>.
    /// </summary>
    public TimeSpan? MaxSignatureAge { get; set; }

    /// <summary>
    ///     Require a <c>created</c> parameter on the signature. Default <c>false</c>
    ///     (Web Bot Auth signatures set it, but the RFC makes it optional).
    /// </summary>
    public bool RequireCreated { get; set; }

    /// <summary>
    ///     Allow-list of RFC 9421 algorithm names. Empty = every algorithm the
    ///     crypto validator supports (<c>ed25519</c>, <c>ecdsa-p256-sha256</c>).
    ///     Restrict to pin an environment to a single algorithm.
    /// </summary>
    public List<string> AllowedAlgorithms { get; set; } = [];

    // ── License capability tokens ───────────────────────────────────────────

    /// <summary>
    ///     Trusted issuer public keys for <c>Authorization: License</c> capability
    ///     tokens. A token is Valid only if one of these keys verifies its
    ///     signature. Empty = no anchors configured → every capability token
    ///     resolves to <c>MissingKey</c>.
    /// </summary>
    public List<CapabilityTrustAnchor> CapabilityTrustAnchors { get; set; } = [];
}

/// <summary>A trusted capability-token issuer key.</summary>
public sealed class CapabilityTrustAnchor
{
    /// <summary>Friendly issuer name — surfaced as the verdict subject fallback and in the dashboard.</summary>
    public string Name { get; set; } = "";

    /// <summary>Base64 Ed25519 public key of the issuer (matches StyloFlow.Licensing key format).</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>Signature algorithm. Default <c>ed25519</c> (StyloFlow license tokens are Ed25519).</summary>
    public string Algorithm { get; set; } = "ed25519";
}