namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     Kinds of things a rate limit can be keyed on. Order matches the spec
///     (see docs/superpowers/specs/2026-05-25-rate-limit-core-design.md):
///     volatile per-actor identity at the top, stable categorical groupings
///     in the middle, last-resort network-level fallback at the bottom.
/// </summary>
public enum RateLimitSubjectKind
{
    /// <summary>Per-fingerprint -- one bucket per distinct PrimarySignature.</summary>
    Fingerprint,

    /// <summary>Whole-class cap by detection.BotType (Scraper / AI / Crawler / ...).</summary>
    BotType,

    /// <summary>Vendor-specific cap by detection.BotName (Googlebot / GPTBot / ClaudeBot / ...).</summary>
    BotName,

    /// <summary>Per-country, ISO-2 code (RU / CN / US / ...).</summary>
    Country,

    /// <summary>Region derived from Country via config-supplied mapping (EU / APAC / ...).</summary>
    Region,

    /// <summary>
    ///     Operator-set label -- the CustomBotName written through the signature-
    ///     labels API. Stable across fingerprint rotation so policy can attach to
    ///     a human-named actor ("partner-feed", "abusive-aggregator-X").
    /// </summary>
    PinnedLabel,

    /// <summary>HMAC-hashed /24 IP subnet. Fallback when fingerprint is unstable.</summary>
    IpSubnet,
}

/// <summary>
///     A single typed predicate that keys a rate-limit bucket. A rule may
///     declare multiple subjects; a request must match ALL of them to consume
///     from the rule's bucket (AND semantics). Composing OR is the operator's
///     job -- they write a second rule.
/// </summary>
public readonly record struct RateLimitSubject(RateLimitSubjectKind Kind, string Value)
{
    public override string ToString() => $"{Kind}:{Value}";
}
