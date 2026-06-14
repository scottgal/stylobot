using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.Test.Policies.Templates;

/// <summary>
///     Round-trip every seed catalog YAML through the resolver with a
///     zero-parameter-override application. Catches regressions where a
///     template adds a placeholder without a default, the predicate text
///     drifts out of the parser's grammar, or the action body shape stops
///     parsing.
/// </summary>
public class SeedCatalogTests
{
    public static IEnumerable<object[]> AllSeedTemplateIds()
    {
        yield return new object[] { "protect-login" };
        yield return new object[] { "protect-checkout" };
        yield return new object[] { "protect-search" };
        yield return new object[] { "protect-admin" };
        yield return new object[] { "protect-expensive-endpoints" };
        yield return new object[] { "protect-content-from-scraping" };
        yield return new object[] { "keep-verified-bots-working" };
        yield return new object[] { "preserve-humans-during-overload" };
        yield return new object[] { "aspnet-adaptive-rate-limit" };
    }

    public static IEnumerable<object[]> ForOperatorTemplateIds()
    {
        // Seed templates that exercise the T-Expr FOR operator natively.
        // Materialized rules MUST carry a Predicate.Sustain node somewhere
        // in the AST.
        yield return new object[] { "protect-expensive-endpoints" };
        yield return new object[] { "preserve-humans-during-overload" };
        yield return new object[] { "aspnet-adaptive-rate-limit" };
    }

    [Theory]
    [MemberData(nameof(AllSeedTemplateIds))]
    public void Template_loads_without_exception(string templateId)
    {
        var store = new YamlTemplateStore();
        var templates = store.LoadEmbeddedCatalog();

        var template = templates.SingleOrDefault(t =>
            string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(template);
        Assert.NotEmpty(template!.Expansion);
    }

    [Theory]
    [MemberData(nameof(AllSeedTemplateIds))]
    public void Zero_override_application_produces_at_least_one_rule(string templateId)
    {
        var store = new YamlTemplateStore();
        var templates = store.LoadEmbeddedCatalog();
        var template = templates.Single(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));

        var app = new TemplateApplication(
            Id: "test-app",
            TemplateId: template.Id,
            AppliedTo: new[] { PolicyScope.Domain("example.com") },
            Parameters: new Dictionary<string, object?>());

        var rules = new TemplateResolver().Resolve(template, app);

        Assert.NotEmpty(rules);
        Assert.All(rules, r =>
        {
            Assert.NotNull(r.Origin);
            Assert.Equal(template.Id, r.Origin!.TemplateId);
        });
    }

    [Theory]
    [MemberData(nameof(ForOperatorTemplateIds))]
    public void FOR_operator_template_materializes_with_a_sustain_node(string templateId)
    {
        var store = new YamlTemplateStore();
        var templates = store.LoadEmbeddedCatalog();
        var template = templates.Single(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));

        var app = new TemplateApplication(
            Id: "test-app",
            TemplateId: template.Id,
            AppliedTo: new[] { PolicyScope.Domain("example.com") },
            Parameters: new Dictionary<string, object?>());

        var rules = new TemplateResolver().Resolve(template, app);

        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => ContainsSustain(r.Predicate));
    }

    private static bool ContainsSustain(Mostlylucid.BotDetection.Policies.Predicate.Predicate predicate) => predicate switch
    {
        Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain => true,
        Mostlylucid.BotDetection.Policies.Predicate.Predicate.And and => and.Children.Any(ContainsSustain),
        Mostlylucid.BotDetection.Policies.Predicate.Predicate.Or or => or.Children.Any(ContainsSustain),
        _ => false
    };
}
