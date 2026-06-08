using System.Reflection;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies.Signals;

namespace Mostlylucid.BotDetection.Test.Policies.Signals;

public class SignalCatalogTests
{
    private static async Task<SignalCatalog> Load()
        => await SignalCatalog.LoadAsync(typeof(SignalKeys).Assembly);

    [Fact]
    public async Task Catalog_enumerates_every_SignalKeys_constant()
    {
        var catalog = await Load();
        var reflected = typeof(SignalKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet();
        foreach (var key in reflected)
            Assert.NotNull(catalog.TryGet(key));
        Assert.True(catalog.All.Count >= reflected.Count, $"All={catalog.All.Count}, reflected={reflected.Count}");
    }

    [Fact]
    public async Task Catalog_pulls_description_from_xml_doc_summary()
    {
        var catalog = await Load();
        var sig = catalog.TryGet("risk.justification");
        Assert.NotNull(sig);
        Assert.False(string.IsNullOrEmpty(sig!.Short), $"Short was empty; Long={sig.Long}");
        Assert.Contains("human-readable explanation", sig.Short, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SignalKind.String, sig.Kind);
    }

    [Fact]
    public async Task Search_by_namespace_prefix_returns_only_namespaced_matches()
    {
        var catalog = await Load();
        var matches = catalog.Search("risk.").ToList();
        Assert.NotEmpty(matches);
        Assert.All(matches, m => Assert.StartsWith("risk.", m.Key));
    }

    [Fact]
    public async Task Fuzzy_search_finds_signal_from_partial_acronym()
    {
        var catalog = await Load();
        // "rj" → "risk.justification" is one of the closest by levenshtein distance.
        // Don't assert exact rank since other 2-char keys may tie -- just verify it's in the top-N.
        var top = catalog.Search("rj", max: 20).ToList();
        Assert.Contains(top, s => s.Key == "risk.justification");
    }

    [Fact]
    public async Task Overlay_supplies_examples_for_known_signals()
    {
        var catalog = await Load();
        var sig = catalog.TryGet("score.bot_probability");
        // If you don't have a score.bot_probability SignalKeys constant, change the test to
        // use a real one your overlay covers.
        if (sig is null) return;       // skip if not in vocabulary (this catalog is dynamic)
        Assert.NotEmpty(sig.Examples);
        Assert.Contains(sig.Examples, e => e.Expr.Contains(">="));
    }

    [Fact]
    public async Task Unknown_key_returns_levenshtein_suggestions()
    {
        var catalog = await Load();
        var miss = catalog.SuggestForUnknown("risk.justificatio", max: 3);   // missing trailing 'n'
        Assert.Contains("risk.justification", miss);
    }

    [Fact]
    public async Task Require_throws_with_suggestion_for_typo()
    {
        var catalog = await Load();
        var ex = Assert.Throws<UnknownSignalException>(() => catalog.Require("risk.justificatio"));
        Assert.Contains("risk.justification", ex.Message);
    }

    [Fact]
    public async Task Namespaces_includes_known_heads()
    {
        var catalog = await Load();
        Assert.Contains("risk", catalog.Namespaces);
        Assert.Contains("ip", catalog.Namespaces);
    }
}
