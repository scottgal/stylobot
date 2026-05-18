using Mostlylucid.BotDetection.ThreatIntel.Providers;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

public class SpamhausDropParserTests
{
    [Fact]
    public void ParseDropFile_HandlesCanonicalLines()
    {
        const string body = """
            192.0.2.0/24 ; SBL12345
            203.0.113.0/24 ; SBL67890
            198.51.100.0/24 ; SBL11111
            """;
        var cidrs = SpamhausDropProvider.ParseDropFile(body).ToList();
        Assert.Equal(new[] { "192.0.2.0/24", "203.0.113.0/24", "198.51.100.0/24" }, cidrs);
    }

    [Fact]
    public void ParseDropFile_SkipsCommentsAndBlankLines()
    {
        const string body = """
            ; Spamhaus DROP List
            ; ===================

            192.0.2.0/24 ; SBL12345

            ; Updated 2026-05-18
            # alternate comment style
            203.0.113.0/24 ; SBL67890
            """;
        var cidrs = SpamhausDropProvider.ParseDropFile(body).ToList();
        Assert.Equal(new[] { "192.0.2.0/24", "203.0.113.0/24" }, cidrs);
    }

    [Fact]
    public void ParseDropFile_TolerantToTrailingWhitespaceAndCarriageReturns()
    {
        // Real-world payloads come in with \r\n + trailing spaces. The parser trims both
        // the line and the post-comment slice so neither shows up in the output.
        const string body = "192.0.2.0/24 ; SBL1   \r\n   203.0.113.0/24;SBL2\r\n\r\n";
        var cidrs = SpamhausDropProvider.ParseDropFile(body).ToList();
        Assert.Equal(new[] { "192.0.2.0/24", "203.0.113.0/24" }, cidrs);
    }

    [Fact]
    public void ParseDropFile_EmptyBody_ReturnsNoEntries()
    {
        Assert.Empty(SpamhausDropProvider.ParseDropFile(""));
        Assert.Empty(SpamhausDropProvider.ParseDropFile("; only comments here\n; more comments"));
    }
}
