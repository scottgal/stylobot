using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;

/// <summary>
///     Pins the UA atom's "contributor, not arbiter of human" contract. A plausible-browser UA is
///     the single most-spoofed surface, so the atom may only lean WEAKLY toward human on it --
///     never the old arbiter-strength <c>-0.15</c> "appears normal" verdict a browser-costumed bot
///     could hide behind. It may still say "bot" strongly. The aggregate, across all contributors,
///     decides human/bot; this atom only observes whether the UA carries a bot marker.
/// </summary>
public class UserAgentAtomHumanLeanTests
{
    private static UserAgentAtom NewAtom(HttpContext http) => new(
        NullLogger<UserAgentAtom>.Instance,
        Options.Create(new BotDetectionOptions()),
        new StubDetectorConfigProvider(),
        new StaticHttpContextAccessor(http));

    private static HttpContext WithUa(string ua)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = ua;
        return http;
    }

    [Fact]
    public async Task Browser_shaped_UA_emits_only_a_WEAK_human_lean_never_an_arbiter_verdict()
    {
        var atom = NewAtom(WithUa(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"));
        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));

        var contributions = await atom.DetectAsync(sink, "test");

        var lean = contributions.Should().ContainSingle().Subject;
        lean.ConfidenceDelta.Should().BeApproximately(-0.05, 1e-9,
            "browser-shaped UA is the most-spoofed surface -> weak human lean (the manifest default), not a verdict");
        lean.ConfidenceDelta.Should().BeGreaterThan(-0.15,
            "must be weaker than the removed arbiter-strength -0.15 human vote");
    }

    [Fact]
    public async Task Bot_marker_UA_still_emits_a_STRONG_bot_signal()
    {
        // A UA carrying a crawler/bot marker is a strong bot signal -- the atom CAN say "bot", it
        // just cannot arbitrate "human".
        var atom = NewAtom(WithUa("Mozilla/5.0 (compatible; ExampleCrawler/2.0; +http://example.com/bot)"));
        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));

        var contributions = await atom.DetectAsync(sink, "test");

        var bot = contributions.Should().ContainSingle().Subject;
        bot.ConfidenceDelta.Should().BeGreaterThan(0.0,
            "a bot marker yields a strong positive bot contribution, not a weak lean");
    }

    [Fact]
    public async Task Self_declaring_contact_url_UA_with_no_bot_keyword_still_emits_a_bot_signal()
    {
        // Polite-crawler self-identification convention: "Name/Version (+URL)" with no
        // "Mozilla/" prefix and no literal bot/crawler/spider/scraper keyword. Regression for
        // the operator's "obvious bot showing as human" report (vJIV0yehw4pCYfvJNvs9zw) -- the
        // UA carried no keyword the old checks recognised, so it decayed toward human on every
        // repeat hit instead of registering as a declared tool.
        var atom = NewAtom(WithUa(
            "Software Security Research/1.0 (+https://reverse-proxies-measurements.softsec.ruhr-uni-bochum.de)"));
        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));

        var contributions = await atom.DetectAsync(sink, "test");

        var bot = contributions.Should().ContainSingle().Subject;
        bot.ConfidenceDelta.Should().BeGreaterThan(0.0,
            "a self-declaring contact-URL UA is a bot marker, not a weak human lean");
    }
}
