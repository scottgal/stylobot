using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     The FOURTH name-consumption site: <see cref="SessionEnrichmentExtensions.ResolveBotName"/>
///     (sessions list, composed row raw fetchers, batch middleware, signature detail).
///     <para>
///         The documented precedence chain MUST survive: cache non-fallback &gt; stored
///         non-fallback &gt; cached-even-if-fallback &gt; persisted lookup dict. The projection may
///         fire ONLY where that chain would otherwise hand back a fallback-shaped value or null —
///         so a real name in any tier still wins, and "Unclassified"/"Unknown"/a UA prefix is
///         treated as unresolved rather than rendered.
///     </para>
/// </summary>
public sealed class ResolveBotNameProvisionalTests
{
    private const string LiveSignature = "6TyG2z5IQguu37X-O3c2xw";

    private static Dictionary<string, string?> Lookup(params (string Key, string? Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void A_real_cache_name_still_beats_the_stored_name_and_the_projection()
    {
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());
        cache.ApplyResolvedNames(new Dictionary<string, string?> { [LiveSignature] = "Googlebot" });

        var resolved = Lookup().ResolveBotName(
            cache, LiveSignature, storedName: "Unclassified", botType: "Scraper", countryCode: "GB");

        Assert.Equal("Googlebot", resolved);
    }

    [Fact]
    public void A_real_stored_name_still_beats_the_projection()
    {
        var resolved = Lookup().ResolveBotName(
            cache: null, LiveSignature, storedName: "Googlebot", botType: "Scraper", countryCode: "GB");

        Assert.Equal("Googlebot", resolved);
    }

    [Fact]
    public void A_real_lookup_name_still_beats_the_projection()
    {
        var resolved = Lookup((LiveSignature, "SemrushBot")).ResolveBotName(
            cache: null, LiveSignature, storedName: null, botType: "Scraper", countryCode: "GB");

        Assert.Equal("SemrushBot", resolved);
    }

    [Fact]
    public void A_fallback_shaped_chain_result_projects_the_row_signals_instead()
    {
        // The live shape: the persisted name is "Unclassified", the pipeline knows the class
        // (Scraper) and the country (GB), and there is no UA.
        var resolved = Lookup().ResolveBotName(
            cache: null, LiveSignature, storedName: "Unclassified",
            botType: "Scraper", countryCode: "GB", userAgent: null);

        Assert.Equal("Scraper 6TyG2z5I · GB", resolved);
    }

    [Fact]
    public void A_cached_fallback_still_projects_rather_than_rendering_the_fallback()
    {
        // Tier 3 ("cached even if fallback-shaped") exists so a fresh value beats nothing --
        // but a fallback-shaped value IS nothing to the operator, so it projects.
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());
        cache.ApplyResolvedNames(new Dictionary<string, string?> { [LiveSignature] = "Unclassified" });

        var resolved = Lookup().ResolveBotName(
            cache, LiveSignature, storedName: null, botType: "Scraper", countryCode: "GB");

        Assert.Equal("Scraper 6TyG2z5I · GB", resolved);
    }

    [Fact]
    public void An_empty_chain_projects_a_total_name_never_null()
    {
        var resolved = Lookup().ResolveBotName(
            cache: null, LiveSignature, storedName: null, botType: null, countryCode: null, userAgent: null);

        Assert.Equal("Client 6TyG2z5I", resolved);
    }
}
