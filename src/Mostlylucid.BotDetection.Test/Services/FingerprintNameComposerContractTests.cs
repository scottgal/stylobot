using FluentAssertions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class FingerprintNameComposerContractTests
{
    [Theory]
    [InlineData("Bingbot",                                              true)]
    [InlineData("Mastodon",                                             true)]
    [InlineData("GPTBot",                                               true)]
    [InlineData("curl-tool",                                            true)]
    [InlineData("Chrome",                                               true)]
    [InlineData("Firefox",                                              true)]
    [InlineData("Mobile Safari",                                        true)]
    [InlineData("Unknown 8c41b2bd",                                     true)]
    [InlineData("Chrome Desktop (missing client hints)",                false)]
    [InlineData("Chrome (privacy-aware)",                               false)]
    [InlineData("Mac Chrome 149 w/ uBlock GB",                          false)]
    [InlineData("Mac Chrome",                                           false)]
    [InlineData("",                                                     false)]
    public void Display_name_matches_one_of_three_allowed_shapes(string candidate, bool allowed)
    {
        var ok = FingerprintNameComposerContract.IsAllowedShape(candidate);
        ok.Should().Be(allowed, $"\"{candidate}\" {(allowed ? "is" : "is NOT")} an allowed shape");
    }
}
