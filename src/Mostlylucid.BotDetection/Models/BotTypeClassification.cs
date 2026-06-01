namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Single source of truth for the "friendly bot type" classification and its
///     associated threat-score gate. Three sites use both - the risk-band determiner,
///     the friendly-bot ledger lookup, and the orchestrator's throttle-status routing
///     fallback - and prior to extraction they each carried their own copy of the
///     five-way <c>BotType</c> match and the <c>0.55</c> magic number.
/// </summary>
public static class BotTypeClassification
{
    /// <summary>
    ///     Threat-score ceiling below which a friendly bot type is still treated as
    ///     friendly. Above this, even a "GoodBot" UA earns the standard treatment -
    ///     a high threat score usually means probing behaviour on top of the friendly
    ///     UA (impersonator scanning .env files while pretending to be Googlebot).
    /// </summary>
    public const double FriendlyThreatGate = 0.55;

    /// <summary>
    ///     True when the bot type is in the friendly set: search engines, fediverse
    ///     link previewers, monitoring tools, IP-verified vendor bots, and other
    ///     "good actor" classifications.
    /// </summary>
    public static bool IsFriendly(BotType? botType) => botType is BotType.SearchEngine
        or BotType.SocialMediaBot
        or BotType.MonitoringBot
        or BotType.GoodBot
        or BotType.VerifiedBot;
}
