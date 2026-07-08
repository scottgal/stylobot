using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Auth;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Sessions;
using Mostlylucid.BotDetection.Orchestration.Sessions.Molecules;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that verifies RFC 9421 HTTP Message
///     Signatures (Web Bot Auth) once per session window, caches the verdict
///     on <see cref="SessionAggregate.WebBotAuthVerdict"/>, and emits the
///     locked Contract C3 identity signals.
/// </summary>
/// <remarks>
///     <para>
///         <b>Security invariant:</b> no raw auth material (signature bytes,
///         signature base string) is ever written to the <see cref="SignalSink"/>
///         or to <see cref="SessionAggregate"/>. Only public metadata — keyid,
///         outcome enum, agent name, algorithm — appears downstream.
///     </para>
///     <para>
///         <b>Inert until real WBA traffic:</b> when the request carries no
///         <c>Signature</c> or <c>Signature-Input</c> headers the atom returns
///         immediately without emitting anything, so it is safe to deploy
///         before real Web Bot Auth traffic arrives.
///     </para>
///     <para>
///         <b>Cache logic:</b>
///         <list type="bullet">
///             <item>No WBA headers → emit nothing, return.</item>
///             <item>Headers present → emit <c>webbotauth.presented</c> beacon.</item>
///             <item>Cache hit (keyid matches cached verdict) → emit from cache, skip crypto.</item>
///             <item>Cache miss or keyid changed → call <see cref="ITokenVerifier.Verify"/>,
///             store result, emit.</item>
///         </list>
///     </para>
///     <para>
///         Priority 23 — runs after identity extraction (Priority 1-8, wave 0)
///         and before the downstream identity consumer chain (e.g.
///         <c>FingerprintApprovalAtom</c> at Priority 24).
///     </para>
/// </remarks>
public sealed class WebBotAuthApprovalAtom : DetectorAtomBase
{
    private readonly ITokenVerifier _verifier;
    private readonly SessionStore _sessionStore;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<WebBotAuthApprovalAtom> _logger;
    private readonly WebBotAuthOptions _options;
    private readonly IdentityArchetypeRegistry _archetypeRegistry;

    // Slice 2 of the session rearchitecture: when present, the verdict is read
    // from / written to the signals-native session layer (molecule projection)
    // instead of the SessionAggregate field. Optional so existing rigs that
    // construct this atom directly fall back to the legacy aggregate.
    private readonly SiteCoordinatorRegistry? _siteCoordinators;

    public WebBotAuthApprovalAtom(
        ITokenVerifier verifier,
        SessionStore sessionStore,
        IHttpContextAccessor httpContextAccessor,
        ILogger<WebBotAuthApprovalAtom> logger,
        IOptions<WebBotAuthOptions> options,
        IdentityArchetypeRegistry archetypeRegistry,
        SiteCoordinatorRegistry? siteCoordinators = null)
        : base(name: "WebBotAuthApproval", category: "WebBotAuth")
    {
        _verifier = verifier;
        _sessionStore = sessionStore;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _options = options.Value;
        _archetypeRegistry = archetypeRegistry;
        _siteCoordinators = siteCoordinators;
    }

    public override int Priority => 23;

    // RequiredSignals intentionally empty — we must run even when no primary
    // signature has been raised yet (e.g. first request for a new fingerprint)
    // so we do our own guard inside DetectAsync.
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var request = context.Request;

        // Guard: atom is inert when no WBA headers are present.
        if (!request.Headers.TryGetValue("Signature-Input", out var signatureInputValues) ||
            !request.Headers.TryGetValue("Signature", out var signatureValues))
            return Task.FromResult(None());

        var signatureInputHeader = signatureInputValues.ToString();
        var signatureHeader = signatureValues.ToString();

        if (string.IsNullOrWhiteSpace(signatureInputHeader) ||
            string.IsNullOrWhiteSpace(signatureHeader))
            return Task.FromResult(None());

        // Beacon: WBA headers are present. Emitted before verify so downstream
        // atoms can gate on presence without waiting for the crypto outcome.
        sink.Raise(SignalKeys.WebBotAuthPresented, sessionId);

        // Cheap keyid parse — avoid full crypto unless keyid changes.
        var presentedKeyId = ParseKeyId(signatureInputHeader);

        // Reconstruct the signed material once, and hash it (non-reversible) so
        // the cache can distinguish a genuine re-presentation of the SAME
        // signature from a different one that merely reuses the (public) keyid.
        var rawValue = signatureInputHeader + "\n" + signatureHeader;
        var presentedSignatureHash = ComputeSignatureHash(rawValue);

        // Session partition: siteId from Host header, fingerprintId from sink.
        // The fingerprint is the CACHE key only — verification never depends on
        // it, so a request with no primary signature yet is still verified.
        var fingerprintId = sink.ReadHint(SignalKeys.PrimarySignature);
        var siteId = ResolveSiteId(request);
        var canCache = !string.IsNullOrEmpty(fingerprintId);

        // Cache hit: same keyid AND same signature, same session window — emit
        // from cache, skip crypto. The signature-hash comparison closes a
        // replay/tamper hole: a request reusing a verified keyid with a
        // different signature must NOT be served the cached "verified" verdict.
        if (canCache)
        {
            var cached = ReadCachedVerdict(siteId, fingerprintId!);
            if (cached is { } &&
                !string.IsNullOrEmpty(presentedKeyId) &&
                string.Equals(cached.KeyId, presentedKeyId, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(cached.SignatureHash) &&
                string.Equals(cached.SignatureHash, presentedSignatureHash, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "WebBotAuthApproval cache hit: fp={FingerprintId} keyid={KeyId} verdict={Verdict}",
                    fingerprintId, cached.KeyId, cached.Verdict);

                EmitFromVerdict(sink, sessionId, cached);
                return Task.FromResult(None());
            }
        }

        // Cache miss, keyid changed, signature changed, or no fingerprint to key
        // on — verify. (rawValue already built above.)

        // Collect covered headers for the verifier (everything except derived
        // @method/@path which come from TokenInput.RequestMethod/RequestPath).
        var coveredHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Headers)
            coveredHeaders[key] = value.ToString();

        var tokenInput = new TokenInput(
            TokenKind.Rfc9421HttpSignature,
            rawValue,
            coveredHeaders,
            request.Method,
            request.Path.Value ?? "/");

        TokenVerdict verdict;
        try
        {
            verdict = _verifier.Verify(tokenInput);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebBotAuthApproval: verifier threw for fp={FingerprintId}", fingerprintId);
            EmitFailedVerdict(sink, sessionId, presentedKeyId, TokenOutcome.Malformed);
            return Task.FromResult(None());
        }

        _logger.LogDebug(
            "WebBotAuthApproval verified: fp={FingerprintId} keyid={KeyId} outcome={Outcome} agent={Agent}",
            fingerprintId, verdict.KeyId, verdict.Outcome, verdict.SubjectName);

        // Determine algorithm — from claims if available.
        var algorithm = verdict.Claims is { } c && c.TryGetValue("alg", out var alg) ? alg : null;

        // Build the cached verdict with public metadata only — no raw auth
        // material. SignatureHash is a non-reversible digest, not the signature.
        var verdictKeyId = verdict.KeyId ?? presentedKeyId ?? string.Empty;
        var newCachedVerdict = new WebBotAuthCachedVerdict(
            verdictKeyId,
            verdict.Outcome,
            verdict.SubjectName,
            algorithm,
            presentedSignatureHash);

        // Write to session aggregate only when we have a fingerprint to key the
        // session window on. Verification + emission are not gated on caching.
        if (canCache)
        {
            _sessionStore.SetWebBotAuthVerdict(siteId, fingerprintId!, newCachedVerdict);

            // Slice 2: also emit the verdict as a session signal so the reader can
            // project it from the signals-native layer (WebBotAuthVerdictMolecule).
            // Additive; the aggregate above stays the synchronous authoritative copy.
            _siteCoordinators?.Contribute(
                siteId,
                fingerprintId!,
                new SessionContribution(
                    RequestId: $"wba:{fingerprintId}:{presentedKeyId}",
                    At: DateTimeOffset.UtcNow,
                    Signals: new[] { WebBotAuthVerdictMolecule.ToSignal(newCachedVerdict) }));
        }

        // Emit identity signals.
        EmitFromVerdict(sink, sessionId, newCachedVerdict);

        // Archetype seed: on a cryptographically verified result, nudge the
        // verified-<botname> centroid toward the current request's identity
        // vector so the registry progressively learns the observed shape of
        // this verified bot. Fail-closed (no archetype = no-op) and no-clobber
        // (bounded EMA inside NudgeArchetype) -- both are enforced by the registry.
        if (verdict.Outcome == TokenOutcome.Valid
            && !string.IsNullOrEmpty(verdict.SubjectName)
            && context is not null)
        {
            var identityVector = IdentityVectorAtom.TryGetVector(context);
            if (identityVector is not null)
            {
                var archetypeId = $"verified-{verdict.SubjectName}";
                _archetypeRegistry.NudgeArchetype(archetypeId, identityVector.AsMemory());
            }
        }

        return Task.FromResult(None());
    }

    // ── private helpers ──────────────────────────────────────────────────────

    /// <summary>
    ///     Reads the current session-window verdict. Slice 2: signals-native
    ///     first (project it out of the session via
    ///     <see cref="WebBotAuthVerdictMolecule"/>), falling back to the legacy
    ///     <c>SessionAggregate.WebBotAuthVerdict</c> when the coordinator isn't
    ///     wired or the session hasn't captured the verdict yet.
    /// </summary>
    private WebBotAuthCachedVerdict? ReadCachedVerdict(string siteId, string fingerprintId)
    {
        if (_siteCoordinators is not null
            && _siteCoordinators.TryGet(siteId, out var coordinator)
            && coordinator!.TryGetSession(fingerprintId, out var session)
            && session is not null)
        {
            var fromSession = WebBotAuthVerdictMolecule.FromSession(session);
            if (fromSession is not null) return fromSession;
        }

        return _sessionStore.TryGet(siteId, fingerprintId)?.WebBotAuthVerdict;
    }

    /// <summary>
    ///     Emits the locked C3 signal set from a cached or freshly-computed verdict.
    ///     Never writes raw auth material — only the public metadata fields.
    /// </summary>
    private static void EmitFromVerdict(SignalSink sink, string sessionId, WebBotAuthCachedVerdict verdict)
    {
        var isVerified = verdict.Verdict == TokenOutcome.Valid;

        sink.Raise($"{SignalKeys.VerifiedBotSigned}:{(isVerified ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.SignatureVerdict}:{verdict.Verdict}", sessionId);

        if (!string.IsNullOrEmpty(verdict.KeyId))
            sink.Raise($"{SignalKeys.VerifiedBotKeyId}:{verdict.KeyId}", sessionId);

        if (!string.IsNullOrEmpty(verdict.SubjectName))
            sink.Raise($"{SignalKeys.WbaVerifiedBotName}:{verdict.SubjectName}", sessionId);

        if (!string.IsNullOrEmpty(verdict.Algorithm))
            sink.Raise($"{SignalKeys.VerifiedBotAlgorithm}:{verdict.Algorithm}", sessionId);
    }

    /// <summary>
    ///     Emits a failed verdict without a cached entry (e.g. verifier threw).
    /// </summary>
    private static void EmitFailedVerdict(
        SignalSink sink, string sessionId, string? keyId, TokenOutcome outcome)
    {
        sink.Raise($"{SignalKeys.VerifiedBotSigned}:false", sessionId);
        sink.Raise($"{SignalKeys.SignatureVerdict}:{outcome}", sessionId);

        if (!string.IsNullOrEmpty(keyId))
            sink.Raise($"{SignalKeys.VerifiedBotKeyId}:{keyId}", sessionId);
    }

    /// <summary>
    ///     Computes a non-reversible SHA-256 (hex) digest of the presented
    ///     signature material. Used only for cache-equality — the digest is
    ///     never emitted to the sink and cannot be reversed to the signature,
    ///     so it does not breach the "no raw auth material" invariant.
    /// </summary>
    internal static string ComputeSignatureHash(string rawValue)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawValue));
        return Convert.ToHexString(digest);
    }

    /// <summary>
    ///     Cheap parse: extracts <c>keyid</c> from the Signature-Input value
    ///     without invoking the full RFC 9421 verifier. Returns <c>null</c>
    ///     when the header doesn't contain a <c>;keyid="..."</c> segment.
    /// </summary>
    private static string? ParseKeyId(string signatureInputHeader)
    {
        // Format: sig1=(<components>);created=...;keyid="<id>";alg=...
        const string needle = ";keyid=\"";
        var idx = signatureInputHeader.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) return null;

        var start = idx + needle.Length;
        var end = signatureInputHeader.IndexOf('"', start);
        return end > start ? signatureInputHeader[start..end] : null;
    }

    /// <summary>
    ///     Resolves the siteId for the session store partition. For the FOSS
    ///     single-tenant deployment the Host header provides adequate
    ///     isolation; multi-tenant commercial deployments may inject a
    ///     dedicated site-id signal upstream.
    /// </summary>
    private static string ResolveSiteId(HttpRequest request)
    {
        var host = request.Host.Host;
        return string.IsNullOrEmpty(host) ? "default" : host;
    }
}