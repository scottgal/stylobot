using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.Test.Policies.Templates;

/// <summary>
///     Pins the FOSS shipped template catalogue at thirteen entries: the ten
///     original seed templates plus the three C7 gap-fillers
///     (block-ai-scrapers, honeypot-trap, shadow-mode). Anyone adding or
///     removing a template under <c>Policies/Templates/Catalog/*.yaml</c>
///     trips this test immediately so the dashboard picker count stays in
///     sync with what the embedded catalogue actually ships.
/// </summary>
public class TemplateRegistryShipsThirteenTests
{
    private readonly TemplateRegistry _registry =
        new(new YamlTemplateStore().LoadEmbeddedCatalog());

    [Fact]
    public void Catalogue_ships_thirteen_templates()
    {
        Assert.Equal(13, _registry.All.Count);
    }

    [Theory]
    [InlineData("block-ai-scrapers")]
    [InlineData("honeypot-trap")]
    [InlineData("shadow-mode")]
    public void C7_gap_filler_template_loads(string templateId)
    {
        var template = _registry.Find(templateId);

        Assert.NotNull(template);
        Assert.False(string.IsNullOrWhiteSpace(template!.DisplayName));
        Assert.NotEmpty(template.Expansion);

        // Round-trip every expansion entry through the resolver so a
        // malformed predicate or action body in any of the three new
        // YAMLs fails here rather than at first-use in the dashboard.
        var app = new TemplateApplication(
            Id: "regression-app",
            TemplateId: template.Id,
            AppliedTo: new[] { PolicyScope.Domain("example.com") },
            Parameters: new Dictionary<string, object?>());
        var rules = new TemplateResolver().Resolve(template, app);
        Assert.Equal(template.Expansion.Count, rules.Count);
    }
}