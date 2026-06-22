using FluentAssertions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class FingerprintNameComposerContractTests
{
    // Structural-only contract: banned shapes are detected by structure
    // (parentheses, slashes, "w/" separator, empty). The contract does NOT
    // maintain an opinion about which multi-word strings count as valid
    // browser families — that's owned by the UA parser (uap-core) and the
    // composer's responsibility to source. A curated allowlist here would
    // silently downgrade real visitors with families we forgot to add
    // (e.g. "Samsung Internet" → "Unknown <hex>"), which is the
    // no-exclusions / centroids-not-rules anti-pattern. The composer
    // (FingerprintNameComposer.Compose, priority 3) returns `family`
    // verbatim, so banned multi-word synthetics like "Mac Chrome" never
    // reach the store in the first place.
    [Theory]
    [InlineData("Bingbot",                                              true)]
    [InlineData("Mastodon",                                             true)]
    [InlineData("GPTBot",                                               true)]
    [InlineData("curl-tool",                                            true)]
    [InlineData("Chrome",                                               true)]
    [InlineData("Firefox",                                              true)]
    [InlineData("Mobile Safari",                                        true)]
    [InlineData("Samsung Internet",                                     true)]
    [InlineData("UC Browser",                                           true)]
    [InlineData("Unknown 8c41b2bd",                                     true)]
    [InlineData("Chrome Desktop (missing client hints)",                false)]
    [InlineData("Chrome (privacy-aware)",                               false)]
    [InlineData("Mac Chrome 149 w/ uBlock GB",                          false)]
    [InlineData("",                                                     false)]
    public void Display_name_matches_one_of_three_allowed_shapes(string candidate, bool allowed)
    {
        var ok = FingerprintNameComposerContract.IsAllowedShape(candidate);
        ok.Should().Be(allowed, $"\"{candidate}\" {(allowed ? "is" : "is NOT")} an allowed shape");
    }
}
