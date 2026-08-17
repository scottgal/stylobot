using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;

/// <summary>
///     Regression for the sqlmap "type=null / throttle-stealth instead of block-hard" P0
///     (gateway-sqlmap-and-likely-other-securitytool-cat issue): SecurityToolAtom
///     (Priority 8) raises a verified, 0.95-confidence <see cref="BotType.MaliciousBot"/>
///     classification onto the shared <c>ua.bot_type</c> signal; UserAgentAtom (Priority
///     10) runs after it, on the same sink, for the SAME request. Before the fix, when
///     UserAgentAtom's own catalog lookup couldn't name the UA, it unconditionally
///     re-raised the signal with its own weaker guess -- clobbering SecurityToolAtom's
///     verdict via last-writer-wins and losing the classification entirely.
/// </summary>
public class SecurityToolClassificationSurvivesUserAgentAtomTests
{
    private const string SqlmapUa = "sqlmap/1.7#stable (http://sqlmap.org)";

    private static HttpContext WithUa(string ua)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = ua;
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        return http;
    }

    private static SecurityToolAtom NewSecurityToolAtom(HttpContext http)
    {
        var fetcher = new Mock<IBotListFetcher>();
        fetcher.Setup(f => f.GetSecurityToolPatternsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SecurityToolPattern>
            {
                new() { Pattern = "sqlmap", Name = "SQLMap", IsRegex = false, Category = "SqlInjection" }
            });

        return new SecurityToolAtom(
            NullLogger<SecurityToolAtom>.Instance,
            Options.Create(new BotDetectionOptions { SecurityTools = new SecurityToolOptions { Enabled = true } }),
            fetcher.Object,
            new StubDetectorConfigProvider(),
            new StaticHttpContextAccessor(http));
    }

    /// <summary>
    ///     A minimal <see cref="ICompiledPatternCache"/> stub standing in for the downloaded
    ///     isbot/crawler-user-agents/coreruleset pattern lists (populated from
    ///     <c>IBotListFetcher</c> in production) -- the real path that flagged sqlmap as
    ///     "some kind of bot" with no specific catalog identity.
    /// </summary>
    private sealed class AlwaysMatchesPatternCache : ICompiledPatternCache
    {
        public IReadOnlyList<System.Text.RegularExpressions.Regex> DownloadedPatterns { get; } = [];
        public IReadOnlyList<ParsedCidrRange> DownloadedCidrRanges { get; } = [];
        public System.Text.RegularExpressions.Regex? GetOrCompileRegex(string pattern) => null;
        public ParsedCidrRange? GetOrParseCidr(string cidr) => null;
        public void UpdateDownloadedPatterns(IEnumerable<string> patterns) { }
        public void UpdateDownloadedCidrRanges(IEnumerable<string> cidrs) { }
        public void Clear() { }

        public bool MatchesAnyPattern(string userAgent, out string? matchedPattern)
        {
            matchedPattern = "sqlmap";
            return true;
        }

        public bool IsInAnyCidrRange(IPAddress ip, out string? matchedCidr)
        {
            matchedCidr = null;
            return false;
        }
    }

    private static UserAgentAtom NewUserAgentAtom(HttpContext http) => new(
        NullLogger<UserAgentAtom>.Instance,
        Options.Create(new BotDetectionOptions()),
        new StubDetectorConfigProvider(),
        new StaticHttpContextAccessor(http),
        patternCache: new AlwaysMatchesPatternCache());

    [Fact]
    public async Task Sqlmap_classification_survives_UserAgentAtom_running_after_it()
    {
        var http = WithUa(SqlmapUa);
        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));

        await NewSecurityToolAtom(http).DetectAsync(sink, "test");
        sink.ReadHint(SignalKeys.UserAgentBotType).Should().Be(nameof(BotType.MaliciousBot));

        // UserAgentAtom runs SECOND (Priority 10 > SecurityToolAtom's 8), on the same sink,
        // for the same request -- exactly the production ordering.
        await NewUserAgentAtom(http).DetectAsync(sink, "test");

        sink.ReadHint(SignalKeys.UserAgentBotType).Should().Be(nameof(BotType.MaliciousBot),
            "SecurityToolAtom's verified catalog match must survive UserAgentAtom running after it -- " +
            "not be clobbered by a weaker generic guess (block-hard vs. falling through to the default policy)");
    }

    [Fact]
    public async Task UserAgentAtom_never_emits_Unknown_as_a_declared_bot_type()
    {
        // No SecurityToolAtom involved here -- this pins UserAgentAtom's OWN fallback in
        // isolation: a UA it can only place via the generic downloaded-pattern-list match
        // (not a named catalog entry) must still resolve to a real behavioural bucket
        // (Scraper -- carries a BotTypeActionPolicies mapping) and never the bare "Unknown"
        // string, which BotTypeActionPolicies has no entry for and silently falls through
        // to the default policy.
        var http = WithUa("SomeUnnamedAutomatedClient/1.0");
        var sink = new SignalSink(maxCapacity: 64, maxAge: TimeSpan.FromMinutes(5));

        await NewUserAgentAtom(http).DetectAsync(sink, "test");

        sink.ReadHint(SignalKeys.UserAgentBotType).Should().NotBe(nameof(BotType.Unknown));
        sink.ReadHint(SignalKeys.UserAgentBotType).Should().Be(nameof(BotType.Scraper));
    }
}
