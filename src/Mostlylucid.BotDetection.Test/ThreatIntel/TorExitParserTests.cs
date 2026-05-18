using Mostlylucid.BotDetection.ThreatIntel.Providers;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

public class TorExitParserTests
{
    [Fact]
    public void ParseExitList_AppendsHostMaskByFamily()
    {
        const string body = """
            192.0.2.1
            203.0.113.42
            2001:db8::1
            """;
        var cidrs = TorExitProvider.ParseExitList(body).ToList();
        Assert.Equal(new[] { "192.0.2.1/32", "203.0.113.42/32", "2001:db8::1/128" }, cidrs);
    }

    [Fact]
    public void ParseExitList_SkipsCommentsAndBlankLines()
    {
        const string body = """
            # generated 2026-05-18
            192.0.2.1

            # another comment
            203.0.113.42
            """;
        var cidrs = TorExitProvider.ParseExitList(body).ToList();
        Assert.Equal(new[] { "192.0.2.1/32", "203.0.113.42/32" }, cidrs);
    }

    [Fact]
    public void ParseExitList_TrimsAnnotationsAfterFirstToken()
    {
        // Some mirrors annotate with the relay nickname after a space.
        const string body = "192.0.2.1 ExitRelayBobsService\n203.0.113.42";
        var cidrs = TorExitProvider.ParseExitList(body).ToList();
        Assert.Equal(new[] { "192.0.2.1/32", "203.0.113.42/32" }, cidrs);
    }

    [Fact]
    public void ParseExitList_EmptyBody_NoEntries()
    {
        Assert.Empty(TorExitProvider.ParseExitList(""));
        Assert.Empty(TorExitProvider.ParseExitList("# only comments here"));
    }
}
