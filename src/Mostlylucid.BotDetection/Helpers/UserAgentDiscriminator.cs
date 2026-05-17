using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Pulls a per-deployment discriminator out of a friendly-bot User-Agent so the
///     dashboard can distinguish individual instances of clustering bots (the canonical
///     case: a fediverse link-preview stampede, where 50 Mastodon instances all hit the
///     same URL within a second).
///
///     <para>
///     Three structural classes of discriminator exist in the wild (see research notes
///     attached to commit history); this extractor handles the dominant one - the
///     <c>+https://hostname/</c> comment convention that traces to RFC 7231 §5.5.3
///     "product comment" syntax and has been adopted by every ActivityPub server
///     (Mastodon, Pleroma, Misskey, Calckey, Akkoma, Pixelfed, Lemmy, Friendica,
///     Hubzilla, PeerTube) plus several AI scrapers.
///     </para>
///
///     <para>
///     Returns null for vendor-home URLs (openai.com, facebook.com, etc.) where the
///     URL is a constant reference back to the bot's documentation rather than a
///     per-deployment identifier. The vendor-home skiplist is the load-bearing piece;
///     without it GPTBot, FacebookExternalHit, and similar would all be labelled with
///     their vendor's own domain and the discriminator would be meaningless.
///     </para>
/// </summary>
internal static class UserAgentDiscriminator
{
    // RFC 7231 product-comment URL. Matches both `+https://host/` (mastodon, lemmy)
    // and `https://host/` without the plus (friendica). Stops at whitespace, `)`,
    // `;`, or `>` so the trailing comment punctuation isn't dragged in.
    private static readonly Regex UrlRegex = new(
        @"\+?(https?://[^\s);>]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Hosts that appear inside friendly-bot UAs as documentation references rather
    // than per-instance identifiers. Adding a host here means we'll *not* surface
    // it as a discriminator - the bot name alone is the identity. Match is on the
    // registrable domain after `www.` stripping.
    private static readonly HashSet<string> VendorHomeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI family - GPTBot, ChatGPT-User, OAI-SearchBot all carry openai.com URLs
        "openai.com",
        // Anthropic
        "anthropic.com",
        "claudebot@anthropic.com",
        // Perplexity
        "perplexity.ai",
        // Meta family - FacebookExternalHit, Facebot
        "facebook.com",
        "www.facebook.com/externalhit_uatext.php",
        // Other social unfurlers whose UAs carry a constant home URL
        "linkedin.com",
        "api.slack.com",
        "discordapp.com",
        "discord.com",
        "telegram.org",
        // Major search engines (Googlebot etc. carry documentation URLs)
        "google.com",
        "developers.google.com",
        "bing.com",
        "www.bing.com",
        "yandex.com",
        "duckduckgo.com",
        "apple.com",
        "applebot.apple.com",
        "baidu.com",
        // Monitoring services
        "pingdom.com",
        "www.pingdom.com",
        "uptimerobot.com",
        "www.uptimerobot.com",
        "site24x7.com",
        "betteruptime.com",
        // Newsfeed readers
        "feedly.com",
        "www.feedly.com",
        "newsblur.com",
        "inoreader.com",
        "newsify.co"
    };

    /// <summary>
    ///     Extracts the per-instance discriminator hostname from a User-Agent, or returns
    ///     null when the UA carries no URL, the URL points at a known vendor-home
    ///     reference, or the UA is empty.
    /// </summary>
    public static string? ExtractDiscriminator(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;

        var match = UrlRegex.Match(userAgent);
        if (!match.Success) return null;

        if (!Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return null;

        // Normalise: strip "www." prefix so "www.mastodon.social" matches "mastodon.social"
        // both in the skiplist and on the dashboard display.
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];

        return VendorHomeHosts.Contains(host) ? null : host;
    }
}
