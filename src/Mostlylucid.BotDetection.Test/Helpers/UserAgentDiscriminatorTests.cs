using Mostlylucid.BotDetection.Helpers;

namespace Mostlylucid.BotDetection.Test.Helpers;

public class UserAgentDiscriminatorTests
{
    [Theory]
    // Fediverse +URL convention - the dominant pattern, all 10 platforms share it
    [InlineData("http.rb/5.2 (Mastodon/4.2.1; +https://mastodon.social/)", "mastodon.social")]
    [InlineData("Mastodon/4.2.0 (compatible; +https://mas.to/)", "mas.to")]
    [InlineData("Pleroma 2.5.0; +https://pleroma.site <admin@pleroma.site>", "pleroma.site")]
    [InlineData("Misskey/13.10.0 (https://misskey.io)", "misskey.io")]
    [InlineData("Lemmy/0.19.3; +https://lemmy.world", "lemmy.world")]
    [InlineData("PeerTube/6.0.0 (+https://peertube.tv)", "peertube.tv")]
    [InlineData("Friendica 'Giant Rhubarb' 2024.06; https://friendica.host", "friendica.host")]
    [InlineData("PixelfedBot v1.0.0 - https://pixelfed.social", "pixelfed.social")]
    [InlineData("Akkoma 3.10; +https://akko.wtf <admin@akko.wtf>", "akko.wtf")]
    [InlineData("Hubzilla/8.4; +https://hub.example.org", "hub.example.org")]
    // www. prefix stripping
    [InlineData("Misskey/13.10 (https://www.misskey.example/)", "misskey.example")]
    public void Extracts_per_instance_hostname_from_friendly_bot_uas(string ua, string expected)
    {
        Assert.Equal(expected, UserAgentDiscriminator.ExtractDiscriminator(ua));
    }

    [Theory]
    // Vendor-home URLs are documentation references, not per-instance identifiers
    [InlineData("Mozilla/5.0 (compatible; GPTBot/1.0; +https://openai.com/gptbot)")]
    [InlineData("Mozilla/5.0 (compatible; ChatGPT-User/1.0; +https://openai.com/bot)")]
    [InlineData("Mozilla/5.0 (compatible; ClaudeBot/1.0; +https://anthropic.com/claudebot)")]
    [InlineData("Mozilla/5.0 (compatible; PerplexityBot/1.0; +https://perplexity.ai/perplexitybot)")]
    [InlineData("Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)")]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)")]
    [InlineData("Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)")]
    [InlineData("LinkedInBot/1.0 (compatible; Mozilla/5.0; +http://www.linkedin.com)")]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
    [InlineData("Mozilla/5.0 (compatible; UptimeRobot/2.0; http://www.uptimerobot.com/)")]
    [InlineData("Feedly/1.0 (+http://www.feedly.com/fetcher.html; 1234 subscribers)")]
    // Vendor-home SUBDOMAINS are documentation references too, not per-instance ids.
    // Meta-ExternalAgent's UA carries developers.facebook.com, which was bloating the
    // display name to "Meta-ExternalAgent developers.facebook.com". Suffix-match on the
    // registered vendor home (facebook.com / google.com / openai.com) drops the subdomain.
    [InlineData("meta-externalagent/1.1 (+https://developers.facebook.com/docs/sharing/webmasters/crawler/)")]
    [InlineData("Mozilla/5.0 (compatible; Google-Extended/1.0; +https://developers.google.com/search/docs/crawling-indexing/overview-google-crawlers)")]
    [InlineData("SomeAiBot/1.0 (+https://labs.openai.com/policies)")]
    // PetalBot (Huawei Petal Search) — regression 2026-08-12: webmaster.petalsearch.com
    // was appended to the bot name, surfacing "Petalsearch Crawler webmaster.petalsearch.com"
    // on Top Bots. The subdomain suffix-match on petalsearch.com drops it.
    [InlineData("Mozilla/5.0 (compatible; PetalBot; +https://webmaster.petalsearch.com/)")]
    [InlineData("PetalBot/2.0 (+https://webmaster.petalsearch.com/; +http://www.aspiegel.com/en/)")]
    public void Returns_null_for_vendor_home_documentation_urls(string ua)
    {
        Assert.Null(UserAgentDiscriminator.ExtractDiscriminator(ua));
    }

    [Theory]
    // Dot-boundary guard: a host that merely ENDS WITH a vendor home but is a different
    // registrable domain is NOT a subdomain, so it keeps its discriminator.
    [InlineData("SomeBot/1.0 (+https://evil-facebook.com/)", "evil-facebook.com")]
    [InlineData("SomeBot/1.0 (+https://notfacebook.com/)", "notfacebook.com")]
    [InlineData("SomeBot/1.0 (+https://myopenai.com/)", "myopenai.com")]
    public void Keeps_discriminator_for_lookalike_hosts_that_are_not_vendor_subdomains(string ua, string expected)
    {
        Assert.Equal(expected, UserAgentDiscriminator.ExtractDiscriminator(ua));
    }

    [Theory]
    // No URL present - regular browsers, no-discriminator bots
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Safari/605.1.15")]
    [InlineData("Twitterbot/1.0")]
    [InlineData("TelegramBot (like TwitterBot)")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_null_when_no_url_in_ua(string? ua)
    {
        Assert.Null(UserAgentDiscriminator.ExtractDiscriminator(ua));
    }

    [Fact]
    public void Stops_url_extraction_at_comment_punctuation()
    {
        // The trailing `)` or `;` must not get sucked into the URL or Uri.TryCreate would
        // reject some otherwise-valid hostnames as invalid characters.
        var result = UserAgentDiscriminator.ExtractDiscriminator(
            "Mastodon/4.2 (+https://example.com); some.other.thing");
        Assert.Equal("example.com", result);
    }

    [Fact]
    public void Ignores_unparseable_urls()
    {
        // Malformed URLs (no scheme, garbled) shouldn't throw - just return null.
        // The regex requires http(s):// so a bare hostname won't match at all.
        Assert.Null(UserAgentDiscriminator.ExtractDiscriminator("SomeBot/1.0 (example.com)"));
    }
}
