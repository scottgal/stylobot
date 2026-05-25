namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     Resolves the operator-set custom label for a fingerprint. The label
///     itself lives in the dashboard's signature-label store
///     (<c>Mostlylucid.BotDetection.UI.Services.ISignatureLabelStore</c>); a
///     thin adapter implements this interface against that store so the
///     rate-limit subject resolver -- which sits in the FOSS bot-detection
///     layer -- can pull labels without taking a dependency on the UI
///     project.
///     <para>
///     The lookup must be cheap (sync, in-memory or short-cached); it runs
///     on the request hot path for every rate-limited request. Return null
///     when the fingerprint has no operator label; the resolver omits the
///     <see cref="RateLimitSubjectKind.PinnedLabel"/> subject in that case.
///     </para>
/// </summary>
public interface IPinnedLabelLookup
{
    /// <summary>
    ///     Returns the operator-set label for <paramref name="primarySignature"/>
    ///     or null when none exists. Implementations should be O(1) -- cache
    ///     the dashboard's label table in memory and refresh on label-store
    ///     write.
    /// </summary>
    string? GetLabel(string primarySignature);
}
