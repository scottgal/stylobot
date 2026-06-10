namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     Entity-vocabulary slot on a <see cref="PolicyScope"/>. The four
///     variants align with the per-request identity signals the FOSS
///     detection pipeline publishes:
///
///     <list type="bullet">
///       <item><description><see cref="NamedBot"/> -- <c>identity.named_bot</c> (e.g. <c>"googlebot"</c>, <c>"chatgpt"</c>).</description></item>
///       <item><description><see cref="BotType"/> -- <c>identity.bot_type</c> (e.g. <c>"scraper"</c>, <c>"headless"</c>).</description></item>
///       <item><description><see cref="HumanBrowser"/> -- <c>identity.human_browser</c> (e.g. <c>"chrome"</c>, <c>"firefox"</c>).</description></item>
///       <item><description><see cref="Fingerprint"/> -- <c>identity.fingerprint_id</c> exact match.</description></item>
///     </list>
///
///     <para>
///     The string values are opaque to this type -- the FOSS detection
///     pipeline owns the value vocabulary. No curated lookup table lives
///     here on purpose.
///     </para>
/// </summary>
public abstract record IdentityScope
{
    /// <summary>Match requests classified as a specific named bot family (case-insensitive).</summary>
    public sealed record NamedBot(string Family) : IdentityScope;

    /// <summary>Match requests classified into a bot category (e.g. scraper, headless).</summary>
    public sealed record BotType(string Category) : IdentityScope;

    /// <summary>Match requests classified as a specific human browser family.</summary>
    public sealed record HumanBrowser(string Family) : IdentityScope;

    /// <summary>Match a single fingerprint by id.</summary>
    public sealed record Fingerprint(string Id) : IdentityScope;

    /// <summary>
    ///     Deterministic, case-folded string projection used as a bucket-key
    ///     fragment (e.g. by the token-bucket throttle handler). Two
    ///     structurally equal <see cref="IdentityScope"/> instances MUST
    ///     produce identical output; the shape does not round-trip.
    ///     Fingerprint ids are case-preserving (they are opaque hashes);
    ///     family / category strings are case-folded so "GoogleBot" and
    ///     "googlebot" hash to the same bucket.
    /// </summary>
    public string ToStableKey() => this switch
    {
        NamedBot n => $"namedbot:{(n.Family ?? string.Empty).ToLowerInvariant()}",
        BotType b => $"bottype:{(b.Category ?? string.Empty).ToLowerInvariant()}",
        HumanBrowser h => $"human:{(h.Family ?? string.Empty).ToLowerInvariant()}",
        Fingerprint f => $"fp:{f.Id ?? string.Empty}",
        _ => "*",
    };
}
