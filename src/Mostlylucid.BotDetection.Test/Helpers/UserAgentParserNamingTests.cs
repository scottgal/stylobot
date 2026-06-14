using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Helpers;

/// <summary>
///     Bug 2 (staging.stylobot.net 2026-06-14). The dashboard misnamed a stampede of bot UAs
///     (Googlebot, Bytespider, meta-externalagent, Edge-on-Windows, mobile Chrome) as
///     "Chrome on macOS" or as each other.
///
///     Triage: the staging Postgres showed one single (ip, ua) pair (primary_signature
///     "EMBTb_6MH7ilcTSEhED0dg") yielding bot_name = "googlebot" most of the time but
///     "Bytespider zhanzhang.toutiao.com" on a handful of requests. The UA was Googlebot
///     throughout. The flicker came from the matcher's persisted DisplayName surface --
///     once a fingerprint's stored display_name drifts to one bot's catalog label,
///     subsequent requests with a DIFFERENT bot UA hitting the same fingerprint inherit
///     the stale name via the FingerprintMatchContributor.EmitDisplayNameSignal "Path 1"
///     hysteresis path. DetectionLedgerExtensions.ResolveDisplayName then prefers that
///     stale signal over the ledger's UA-derived BotName.
///
///     These tests pin the correct precedence:
///       1. A fresh UA pattern match (ua.bot_name set by UserAgentContributor) is the
///          authoritative per-request identity. Stored fingerprint name is a fallback,
///          not a veto.
///       2. UserAgentParser doesn't misclassify the common bot UA shapes as "Chrome".
/// </summary>
public class UserAgentParserNamingTests
{
    // ----- Bug repro: stale matched.DisplayName must not override fresh ua.bot_name ----

    [Fact]
    public void Compose_FreshBotName_BeatsPreviousMatcherName()
    {
        // Scenario from staging: the matched fingerprint's persisted DisplayName is
        // "Bytespider zhanzhang.toutiao.com" (set on an earlier observation). The CURRENT
        // request UA is a real Googlebot UA and UserAgentContributor classified it
        // correctly: ua.bot_name = "Googlebot". The composer MUST return "Googlebot",
        // not the previous Bytespider name.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>
            {
                [SignalKeys.UserAgentBotName] = "Googlebot",
                [SignalKeys.UserAgent] = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"
            },
            previousName: "Bytespider zhanzhang.toutiao.com");

        Assert.NotNull(name);
        Assert.StartsWith("Googlebot", name);
        Assert.DoesNotContain("Bytespider", name);
    }

    [Fact]
    public void Compose_FreshBotName_BeatsStaleChromeOnMacOsName()
    {
        // Another flavour: the fingerprint was first labelled "Chrome on macOS" via the
        // matcher's vector-fallback path (matcher won the race over UserAgentContributor
        // on request 1). Request 2 arrives as a self-declared Mastodon UA; the composer
        // MUST recompose to "Mastodon" rather than echo the stale browser-shaped name.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>
            {
                [SignalKeys.UserAgentBotName] = "Mastodon",
                [SignalKeys.UserAgent] = "http.rb/5.2 (Mastodon/4.3.0; +https://mastodon.social/)"
            },
            previousName: "Chrome on macOS (privacy headers)");

        Assert.NotNull(name);
        Assert.StartsWith("Mastodon", name);
        Assert.DoesNotContain("Chrome", name);
    }

    [Fact]
    public void Compose_FreshArchetype_BeatsPreviousMatcherName()
    {
        // Priority 2 case: archetype kind is human-browser, so the freshly-matched
        // archetype name should win over a stale stored name.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>
            {
                [SignalKeys.IdentityArchetypeName] = "Firefox Desktop",
                [SignalKeys.IdentityArchetypeKind] = "human-browser"
            },
            previousName: "Bytespider on Android");

        Assert.NotNull(name);
        Assert.StartsWith("Firefox Desktop", name);
        Assert.DoesNotContain("Bytespider", name);
    }

    [Fact]
    public void Compose_FreshFamily_BeatsPreviousMatcherName()
    {
        // Priority 3 case: the fresh signals carry UA family + OS. That's also more
        // authoritative than a stale fingerprint name -- the stored name was right WHEN
        // it was written, but a different family on the current request means we have
        // newer evidence.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>
            {
                [SignalKeys.UserAgentFamily] = "Edge",
                [SignalKeys.UserAgentOs] = "Windows"
            },
            previousName: "Chrome on macOS (header drift)");

        Assert.NotNull(name);
        Assert.StartsWith("Edge on Windows", name);
        Assert.DoesNotContain("Chrome on macOS", name);
    }

    [Fact]
    public void Compose_NoFreshSignal_KeepsPreviousName()
    {
        // The hysteresis behaviour that exists is correct WHEN there's nothing fresh to
        // upgrade to: a Priority-4 nothing-to-name fresh result must yield to the
        // previous name so the visible label doesn't flicker. This locks in that the
        // bug fix doesn't accidentally wipe legitimate hysteresis.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            previousName: "Chrome on Windows");

        Assert.Equal("Chrome on Windows", name);
    }

    // ----- UserAgentParser behaviour: no surprise "Chrome" misclassification ----

    [Theory]
    [InlineData(
        "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
        "Googlebot")]
    [InlineData(
        "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",
        "bingbot")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/[phone] Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
        "Googlebot")]
    public void Parse_KnownBotUa_DoesNotReturnChrome(string ua, string expectedFamilyContains)
    {
        var (family, _) = UserAgentParser.Parse(ua);
        Assert.NotEqual("Chrome", family);
        Assert.Contains(expectedFamilyContains, family, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("meta-externalagent/1.1 (+https://developers.facebook.com/docs/sharing/webmasters/crawler)")]
    [InlineData("Mozilla/5.0 (compatible; crawler)")]
    public void Parse_KeywordCrawlerUa_DoesNotReturnChrome(string ua)
    {
        var (family, _) = UserAgentParser.Parse(ua);
        Assert.NotEqual("Chrome", family);
    }

    [Theory]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36 Edg/148.0.0.0",
        "Edge")]
    public void Parse_EdgeUa_ReturnsEdgeNotChrome(string ua, string expectedFamily)
    {
        var (family, _) = UserAgentParser.Parse(ua);
        Assert.Equal(expectedFamily, family);
    }

    [Theory]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Mobile Safari/537.36",
        "Android")]
    public void Parse_AndroidChromeUa_OsIsAndroid(string ua, string expectedOs)
    {
        var os = UserAgentParser.ExtractOs(ua);
        Assert.Equal(expectedOs, os);
    }

    // ----- BotPatternLoader: every UA shape we saw has a YAML entry to recover Priority 1 ----

    [Theory]
    [InlineData(
        "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
        "Googlebot")]
    [InlineData(
        "meta-externalagent/1.1 (+https://developers.facebook.com/docs/sharing/webmasters/crawler)",
        "Meta-ExternalAgent")]
    [InlineData(
        "http.rb/5.2 (Mastodon/4.3.0; +https://mastodon.social/)",
        "Mastodon")]
    public void BotPatternLoader_KnownBotUa_MatchesPattern(string ua, string expectedBotName)
    {
        var (botType, botName) = BotPatternLoader.Default.MatchUserAgent(ua);
        Assert.NotNull(botType);
        Assert.Equal(expectedBotName, botName);
    }
}