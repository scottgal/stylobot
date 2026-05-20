namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Single source of truth for the "friendly bot type" classification and its
///     associated threat-score gate. Three sites use both - the risk-band determiner,
///     the friendly-bot ledger lookup, and the orchestrator's throttle-status routing
///     fallback - and prior to extraction they each carried their own copy of the
///     five-way <c>BotType</c> match and the <c>0.55</c> magic number. A new
///     friendly type added to <see cref="BotType"/> would otherwise silently miss
///     two of the three sites.
/// </summary>
internal static class BotTypeClassification
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
    ///     "good actor" classifications. Returns false for null / Unknown / Scraper /
    ///     MaliciousBot / ExploitScanner / AiBot / Tool / ClickFraud.
    /// </summary>
    public static bool IsFriendly(BotType? botType) => botType is BotType.SearchEngine
        or BotType.SocialMediaBot
        or BotType.MonitoringBot
        or BotType.GoodBot
        or BotType.VerifiedBot;

    /// <summary>
    ///     Name-level fallback used when bot_type propagation failed but the UA bot_name
    ///     clearly identifies a known-good crawler. The set mirrors the search-engines /
    ///     ai-scrapers / monitoring YAML pattern files' bot_name fields for entries that
    ///     are explicitly classified as friendly. New friendly UAs must be added here too
    ///     for the risk-band-pin to apply when bot_type ends up "Unknown" -- the YAML's
    ///     bot_type field is the source of truth, this is a safety net.
    /// </summary>
    public static bool IsKnownFriendlyName(string botName)
    {
        if (string.IsNullOrEmpty(botName)) return false;
        return botName switch
        {
            // Search engines (search-engines.bot-patterns.yaml)
            "Googlebot" or "Bingbot" or "DuckDuckBot" or "Baidu Search" or "Yahoo Search"
                or "Yandex Search" or "Sogou" or "Qwant" or "Mojeek" or "ChatGPT-User"
                or "Brave Search" or "Kagi" or "PerplexityBot" => true,
            // AI scrapers that have published vendor identities (subset of ai-scrapers.bot-patterns.yaml
            // that's "good actor" in the operator's eyes -- excludes scrapers without a known vendor).
            // Operators can opt these out via policy if they want to block AI training.
            "GPTBot" or "OAI-SearchBot" or "ClaudeBot" or "Claude-Web" or "Gemini" or "Google-Extended"
                or "anthropic-ai" or "cohere-ai" or "Amazonbot" or "Applebot" or "Applebot-Extended"
                or "facebookexternalhit" or "FacebookBot" or "Meta-ExternalAgent" or "Meta-ExternalFetcher"
                => true,
            // Social media / link previewers (social-media.bot-patterns.yaml)
            "Twitterbot" or "LinkedInBot" or "Slackbot" or "Discordbot" or "TelegramBot"
                or "WhatsApp" or "Pinterest" or "RedditBot" or "Tumblr" or "SkypeUriPreview"
                => true,
            // Fediverse instances (variants like "Mastodon mastodon.social" all start with these
            // prefixes; the FingerprintNameComposer appends instance domains as discriminator).
            var s when s.StartsWith("Mastodon", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("Pleroma", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("Misskey", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("Lemmy", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("Pixelfed", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("Funkwhale", StringComparison.OrdinalIgnoreCase)
                    => true,
            // Monitoring (monitoring.bot-patterns.yaml subset)
            "UptimeRobot" or "Pingdom" or "Datadog" or "NewRelic" or "Site24x7"
                => true,
            _ => false
        };
    }
}
