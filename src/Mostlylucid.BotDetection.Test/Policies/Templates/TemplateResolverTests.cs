using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.Test.Policies.Templates;

/// <summary>
///     Pin the core resolver contract: parameter merge, expansion+scope cross,
///     deterministic ids, origin stamping. These are the building blocks T2
///     leans on for override-divergence detection.
/// </summary>
public class TemplateResolverTests
{
    private static Template OneEntryTemplate(string id = "t1", string suffix = "main",
        string predicate = "ua.is_bot = true", string action = "{ kind: block }") =>
        new(
            Id: id,
            DisplayName: id,
            Description: "test",
            Category: "test",
            Parameters: Array.Empty<TemplateParameter>(),
            Expansion: new[]
            {
                new TemplateExpansionEntry(suffix, predicate, action, Priority: 100, Mode: "live")
            });

    private static TemplateApplication Application(string templateId, params PolicyScope[] scopes) =>
        new(
            Id: "app1",
            TemplateId: templateId,
            AppliedTo: scopes,
            Parameters: new Dictionary<string, object?>());

    [Fact]
    public void Single_expansion_single_scope_produces_one_rule_with_origin_set()
    {
        var t = OneEntryTemplate();
        var app = Application(t.Id, PolicyScope.Domain("acme.com"));

        var rules = new TemplateResolver().Resolve(t, app);

        var rule = Assert.Single(rules);
        Assert.Equal(PolicyScope.Domain("acme.com"), rule.Scope);
        Assert.IsType<PolicyAction.Block>(rule.Action);
        Assert.NotNull(rule.Origin);
        Assert.Equal(t.Id, rule.Origin!.TemplateId);
        Assert.Equal(app.Id, rule.Origin.ApplicationId);
        Assert.Equal("main", rule.Origin.ExpansionSuffix);
    }

    [Fact]
    public void N_expansion_M_scopes_produces_N_times_M_rules()
    {
        var t = new Template(
            Id: "multi",
            DisplayName: "multi",
            Description: "test",
            Category: "test",
            Parameters: Array.Empty<TemplateParameter>(),
            Expansion: new[]
            {
                new TemplateExpansionEntry("a", "ua.is_bot = true", "{ kind: block }", 100, "live"),
                new TemplateExpansionEntry("b", "ua.is_bot = false", "{ kind: allow }", 200, "live"),
                new TemplateExpansionEntry("c", "fingerprint.integrity_score < 0.5", "{ kind: block }", 50, "live")
            });
        var app = Application(t.Id, PolicyScope.Domain("a.com"), PolicyScope.Domain("b.com"));

        var rules = new TemplateResolver().Resolve(t, app);

        Assert.Equal(3 * 2, rules.Count);
    }

    [Fact]
    public void Parameter_default_is_substituted_when_application_does_not_override()
    {
        var t = new Template(
            Id: "p-default",
            DisplayName: "p",
            Description: "",
            Category: "",
            Parameters: new[] { new TemplateParameter("rpm", "int", 42) },
            Expansion: new[]
            {
                new TemplateExpansionEntry("e", "ua.is_bot = false", "{ kind: rate_limit, rpm: {{ rpm }} }", 100, "live")
            });

        var rules = new TemplateResolver().Resolve(t, Application(t.Id, PolicyScope.Wildcard()));

        var rule = Assert.Single(rules);
        var rl = Assert.IsType<PolicyAction.RateLimit>(rule.Action);
        Assert.Equal(42, rl.RequestsPerMinute);
    }

    [Fact]
    public void Application_parameter_value_wins_over_template_default()
    {
        var t = new Template(
            Id: "p-override",
            DisplayName: "p",
            Description: "",
            Category: "",
            Parameters: new[] { new TemplateParameter("rpm", "int", 42) },
            Expansion: new[]
            {
                new TemplateExpansionEntry("e", "ua.is_bot = false", "{ kind: rate_limit, rpm: {{ rpm }} }", 100, "live")
            });
        var app = new TemplateApplication(
            "app1", t.Id,
            new[] { PolicyScope.Wildcard() },
            new Dictionary<string, object?> { ["rpm"] = 7 });

        var rules = new TemplateResolver().Resolve(t, app);

        var rule = Assert.Single(rules);
        var rl = Assert.IsType<PolicyAction.RateLimit>(rule.Action);
        Assert.Equal(7, rl.RequestsPerMinute);
    }

    [Fact]
    public void Empty_applied_to_list_falls_back_to_wildcard_scope()
    {
        var t = OneEntryTemplate();
        var app = new TemplateApplication(
            "app1", t.Id,
            AppliedTo: Array.Empty<PolicyScope>(),
            Parameters: new Dictionary<string, object?>());

        var rules = new TemplateResolver().Resolve(t, app);

        var rule = Assert.Single(rules);
        Assert.True(rule.Scope.IsWildcard);
    }

    [Fact]
    public void Same_inputs_produce_same_rule_id_deterministically()
    {
        var t = OneEntryTemplate();
        var app = Application(t.Id, PolicyScope.Domain("acme.com"));

        var first = new TemplateResolver().Resolve(t, app)[0].Id;
        var second = new TemplateResolver().Resolve(t, app)[0].Id;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_scopes_produce_different_rule_ids()
    {
        var t = OneEntryTemplate();
        var app = Application(t.Id, PolicyScope.Domain("a.com"), PolicyScope.Domain("b.com"));

        var rules = new TemplateResolver().Resolve(t, app);

        Assert.Equal(2, rules.Count);
        Assert.NotEqual(rules[0].Id, rules[1].Id);
    }

    [Fact]
    public void Every_materialized_rule_carries_origin_with_template_app_and_suffix()
    {
        var t = new Template(
            Id: "origin-stamp",
            DisplayName: "x",
            Description: "",
            Category: "",
            Parameters: Array.Empty<TemplateParameter>(),
            Expansion: new[]
            {
                new TemplateExpansionEntry("a", "ua.is_bot = true", "{ kind: block }", 100, "live"),
                new TemplateExpansionEntry("b", "ua.is_bot = false", "{ kind: allow }", 200, "live"),
            });
        var app = Application(t.Id, PolicyScope.Domain("acme.com"));

        var rules = new TemplateResolver().Resolve(t, app);

        Assert.Equal(2, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.NotNull(r.Origin);
            Assert.Equal(t.Id, r.Origin!.TemplateId);
            Assert.Equal(app.Id, r.Origin.ApplicationId);
        });
        Assert.Equal("a", rules[0].Origin!.ExpansionSuffix);
        Assert.Equal("b", rules[1].Origin!.ExpansionSuffix);
    }

    [Fact]
    public void Substitute_throws_on_unknown_placeholder()
    {
        var template = "value is {{ missing }}";
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        Assert.Throws<FormatException>(() => TemplateResolver.Substitute(template, parameters));
    }

    [Fact]
    public void Substitute_formats_numeric_parameters_with_invariant_culture()
    {
        var template = "score > {{ x }}";
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = 0.75m
        };

        var result = TemplateResolver.Substitute(template, parameters);

        Assert.Equal("score > 0.75", result);
    }

    [Fact]
    public void Stable_rule_id_changes_when_template_id_changes()
    {
        var s = PolicyScope.Domain("acme.com");
        var a = TemplateResolver.ComputeStableRuleId("template-a", "app1", "main", s);
        var b = TemplateResolver.ComputeStableRuleId("template-b", "app1", "main", s);

        Assert.NotEqual(a, b);
    }
}
