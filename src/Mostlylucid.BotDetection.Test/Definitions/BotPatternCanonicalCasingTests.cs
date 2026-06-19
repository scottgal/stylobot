using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Definitions;

/// <summary>
///     Pins <see cref="BotPatternLoader.FindCanonicalCasing"/> -- the SINGLE
///     write-boundary normaliser called by IFingerprintStore and the dashboard
///     broadcast event builder. Whatever spelling a contributor or LLM emits
///     ("googlebot", "GOOGLEBOT", regex-capture "Googlebot/2.1") folds to the
///     catalog's canonical name BEFORE the row is persisted or broadcast.
///     Without this normalisation, the same identity surfaces as N rows
///     because writers race to land different strings in the same BotName
///     field -- the staging bug that motivated this helper.
/// </summary>
public class BotPatternCanonicalCasingTests
{
    [Theory]
    [InlineData("googlebot",   "Googlebot")]
    [InlineData("Googlebot",   "Googlebot")]
    [InlineData("GOOGLEBOT",   "Googlebot")]
    [InlineData("  Googlebot ", "Googlebot")] // surrounding whitespace tolerated
    public void Catalog_name_normalises_to_canonical_casing(string input, string expected)
    {
        Assert.Equal(expected, BotPatternLoader.Default.FindCanonicalCasing(input));
    }

    [Fact]
    public void Unknown_name_returns_null_so_caller_keeps_input_as_is()
    {
        // Operator-set labels, fediverse instance suffixes not in the catalog, ad-hoc
        // matcher labels -- these pass through unchanged. The normaliser is opt-in:
        // it only rewrites names it KNOWS, otherwise the caller stores the input.
        Assert.Null(BotPatternLoader.Default.FindCanonicalCasing("My Custom Bot 1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_blank_returns_null(string? input)
    {
        Assert.Null(BotPatternLoader.Default.FindCanonicalCasing(input));
    }

    [Fact]
    public void Discriminator_suffix_canonicalises_head_preserves_tail()
    {
        // FingerprintNameComposer appends instance hostnames for fediverse UAs that
        // carry a +URL comment -- "Mastodon mastodon.social" is the existing pattern.
        // The head ("mastodon") must canonicalise; the tail (instance) stays attached.
        var result = BotPatternLoader.Default.FindCanonicalCasing("mastodon mastodon.social");
        Assert.NotNull(result);
        Assert.StartsWith("Mastodon", result, System.StringComparison.Ordinal);
        Assert.EndsWith("mastodon.social", result);
    }
}