using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     The display tier's name disposition: a stored/resolved name is shown when it is a REAL
///     name, and otherwise the row's own knowledge is projected into a provisional name.
///     <para>
///         Operator ruling 2026-09-09 ("the id is fixed, the name is a projection that the full
///         name must eventually REPLACE"): a fallback-shaped value — <c>Unclassified</c> is the
///         live case, the value the induced-name writer persisted before its persist gate landed
///         — means "we hold no name yet", NOT a name. Rendering it as one is what made the
///         dashboard show <c>Unclassified</c> while the pipeline knew the class, the country and
///         the fingerprint id. Treating it as unresolved and composing
///         <see cref="FingerprintNameComposer.ComposeProvisional"/> from the row gives
///         <c>Scraper 6TyG2z5I · GB</c> instead.
///     </para>
///     <para>
///         Nothing here is written back: no slot, no <c>name_history</c>, no store call. The
///         projection is a pure function of (stored name, signature, row signals), so a later
///         real name lands in its slot uncontested and wins on the next render.
///     </para>
/// </summary>
public static class ProvisionalNameProjection
{
    /// <summary>
    ///     Resolve the name a row should display. Returns <paramref name="storedName"/> when it is
    ///     a real name; otherwise composes the provisional projection from the row's class
    ///     (<paramref name="botType"/>), qualifier (<paramref name="countryCode"/>) and
    ///     fingerprint discriminator (<paramref name="signature"/>). Total: never null/empty.
    /// </summary>
    public static string Resolve(
        string? storedName,
        string? signature,
        string? botType,
        string? countryCode,
        string? userAgent)
    {
        if (!FingerprintNameComposer.IsFallback(storedName)) return storedName!;

        var signals = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(botType))
            signals[SignalKeys.UserAgentBotType] = botType;
        if (!string.IsNullOrEmpty(countryCode))
            signals[SignalKeys.GeoCountryCode] = countryCode;

        return FingerprintNameComposer.ComposeProvisional(signals, signature, userAgent);
    }
}
