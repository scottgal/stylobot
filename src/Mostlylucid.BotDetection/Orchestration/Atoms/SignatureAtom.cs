using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     SensorAtom (per Taxonomy.md) that computes the canonical
///     <see cref="MultiFactorSignatures"/> for a request, collects header
///     hashes for progressive identity resolution, and raises the primary
///     signature hint on the sink.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>SignatureContributor</c>. Priority 1 -- runs before every other
///         atom in Wave 0.
///     </para>
///     <para>
///         <b>Atom-holds-rich-state pattern.</b> The signature object and
///         header-hash dictionary are the archetype of "large, structured,
///         useful-to-many-downstream" state. Per the state-vs-signal PII rule
///         the sink is the wrong carrier for rich objects -- it stores
///         signals as short strings inside a bounded sliding window. This
///         atom therefore keeps the rich payload on
///         <see cref="HttpContext.Items"/> under
///         <see cref="MultifactorKey"/> / <see cref="HeaderHashesKey"/>,
///         which the ASP.NET runtime evicts at request completion. Downstream
///         atoms retrieve via <see cref="TryGetMultifactor"/> /
///         <see cref="TryGetHeaderHashes"/>.
///     </para>
///     <para>
///         The sink still learns "signature computed" (ran-vs-value ledger)
///         and the primary-signature hash (Model 2 hint -- a hash is the
///         same identifier grade as fingerprint IDs already resident in the
///         sink).
///     </para>
/// </remarks>
public sealed class SignatureAtom : DetectorAtomBase
{
    /// <summary>Key on <see cref="HttpContext.Items"/> for the rich <see cref="MultiFactorSignatures"/> object.</summary>
    public const string MultifactorKey = "stylobot.signature.multifactor";

    /// <summary>Key on <see cref="HttpContext.Items"/> for the header-hash dictionary.</summary>
    public const string HeaderHashesKey = "stylobot.signature.header_hashes";

    private readonly ILogger<SignatureAtom> _logger;
    private readonly MultiFactorSignatureService _signatureService;
    private readonly HeaderHashCollector _headerHashCollector;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public SignatureAtom(
        ILogger<SignatureAtom> logger,
        MultiFactorSignatureService signatureService,
        HeaderHashCollector headerHashCollector,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "Signature", category: "Signature")
    {
        _logger = logger;
        _signatureService = signatureService;
        _headerHashCollector = headerHashCollector;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 1;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        try
        {
            var signatures = _signatureService.GenerateSignatures(context);

            // Impersonation (debug/ops): a key with AllowImpersonation can pin the request's
            // primary_signature to a target so detection resolves against THAT stored user's
            // fingerprint -- the seam for reproducing a single user's scoring to diagnose
            // fingerprint issues. Override before the rich object + sink hint are published, so
            // every downstream consumer (fingerprint resolution, centroid match, drift, dashboard)
            // sees the impersonated identity. Forced read-only upstream via
            // IsLearningSuppressedByApiKey, so it can never poison the target's model. Logged at
            // Warning + raised on the sink for auditability -- impersonation must never be silent.
            var impersonationTarget = context.GetImpersonationTarget();
            if (!string.IsNullOrEmpty(impersonationTarget))
            {
                _logger.LogWarning(
                    "Request impersonating identity {Target} (was {Original}) via API key {Key} -- read-only, detection-identity only",
                    impersonationTarget, signatures.PrimarySignature, context.GetApiKeyContext()?.KeyName ?? "?");
                signatures.PrimarySignature = impersonationTarget;
                sink.Raise($"signature.impersonated:{impersonationTarget}", sessionId);
            }

            // Rich object -> HttpContext.Items (request-scoped, auto-evicted).
            context.Items[MultifactorKey] = signatures;

            // Primary signature hash -> sink (thin identifier, same grade as
            // fingerprint IDs). Ledger presence signal for the ran-vs-value
            // pattern so downstream can tell "we ran but produced no
            // signature" apart from "we didn't run".
            sink.Raise("signature.computed", sessionId);
            if (!string.IsNullOrEmpty(signatures.PrimarySignature))
                sink.Raise($"{SignalKeys.PrimarySignature}:{signatures.PrimarySignature}", sessionId);

            var headerHashes = _headerHashCollector.CollectHashes(context.Request);
            if (headerHashes.Count > 0)
            {
                context.Items[HeaderHashesKey] = headerHashes;
                sink.Raise($"signature.header_hash_count:{headerHashes.Count}", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error computing unified signature");
        }

        return Task.FromResult(None());
    }

    /// <summary>
    ///     Retrieves the <see cref="MultiFactorSignatures"/> written by this
    ///     atom for the current request. Returns <c>null</c> if the atom did
    ///     not run (e.g. HttpContext missing) or if signature computation
    ///     failed. Downstream atoms should call this instead of trying to
    ///     read the rich object off the sink.
    /// </summary>
    public static MultiFactorSignatures? TryGetMultifactor(HttpContext context)
        => context.Items.TryGetValue(MultifactorKey, out var v) && v is MultiFactorSignatures sig
            ? sig
            : null;

    /// <summary>
    ///     Retrieves the header-hash dictionary written by this atom for the
    ///     current request. Returns <c>null</c> if none were collected.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? TryGetHeaderHashes(HttpContext context)
        => context.Items.TryGetValue(HeaderHashesKey, out var v)
            && v is IReadOnlyDictionary<string, string> dict
                ? dict
                : null;
}
