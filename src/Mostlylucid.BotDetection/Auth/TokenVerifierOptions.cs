namespace Mostlylucid.BotDetection.Auth;

/// <summary>
///     Configuration surface for <see cref="ITokenVerifier"/> (contract C7).
///     Holds every knob for both token kinds — RFC 9421 HTTP signatures and
///     generic signed bearer tokens. Bound from <c>BotDetection:TokenVerifier</c>.
///     <para>
///         The signed-bearer-token knobs are deliberately generic: FOSS knows how
///         to verify a canonical-JSON-signed token against trust anchors, but not
///         what the token <i>means</i>. A consumer that layers meaning on top
///         (e.g. a commercial license atom) points <see cref="SignedTokenSubjectClaim"/>
///         / <see cref="SignedTokenExpiryClaim"/> at its own claim names and reads
///         the surfaced claims; trust anchors live here so all <i>verification</i>
///         config has one home.
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

    // ── Signed bearer tokens ────────────────────────────────────────────────

    /// <summary>
    ///     Trusted issuer public keys for signed bearer tokens. A token is Valid
    ///     only if one of these keys verifies its signature. Empty = no anchors
    ///     configured → every signed bearer token resolves to <c>MissingKey</c>.
    /// </summary>
    public List<TokenTrustAnchor> TrustAnchors { get; set; } = [];

    /// <summary>
    ///     The JSON field that carries the base64 signature (and is excluded from
    ///     the canonical signable content). Default <c>signature</c>.
    /// </summary>
    public string SignedTokenSignatureField { get; set; } = "signature";

    /// <summary>
    ///     The claim whose value is surfaced as <see cref="TokenVerdict.SubjectName"/>.
    ///     Default <c>sub</c> (a consumer with its own convention overrides it).
    /// </summary>
    public string SignedTokenSubjectClaim { get; set; } = "sub";

    /// <summary>
    ///     The claim checked for expiry (ISO-8601 string or Unix seconds). Absent
    ///     from the token = no expiry. Default <c>exp</c>.
    /// </summary>
    public string SignedTokenExpiryClaim { get; set; } = "exp";
}

/// <summary>A trusted signed-bearer-token issuer key.</summary>
public sealed class TokenTrustAnchor
{
    /// <summary>Friendly issuer name — surfaced as the verdict subject fallback and in the dashboard.</summary>
    public string Name { get; set; } = "";

    /// <summary>Base64 public key of the issuer.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>Signature algorithm. Default <c>ed25519</c>.</summary>
    public string Algorithm { get; set; } = "ed25519";
}