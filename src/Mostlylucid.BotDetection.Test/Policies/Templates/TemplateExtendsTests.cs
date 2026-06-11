using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.Test.Policies.Templates;

/// <summary>
///     Pin T5's <c>extends:</c> inheritance semantics. Each test goes through
///     <see cref="TemplateRegistry"/> -- the registry is the seam where the
///     validator runs and where the flattened-template view is produced.
/// </summary>
public class TemplateExtendsTests
{
    private static Template Parent(
        string id = "parent",
        IReadOnlyList<TemplateParameter>? parameters = null,
        IReadOnlyList<TemplateExpansionEntry>? expansion = null) =>
        new(
            Id: id,
            DisplayName: id,
            Description: "parent",
            Category: "test",
            Parameters: parameters ?? new[] { new TemplateParameter("rpm", "int", 6) },
            Expansion: expansion ?? new[]
            {
                new TemplateExpansionEntry(
                    Suffix: "main",
                    Predicate: "ua.is_bot = false",
                    Action: "{ kind: rate_limit, rpm: {{ rpm }} }",
                    Priority: 100,
                    Mode: "live")
            });

    private static Template Child(
        string id,
        string extends,
        IReadOnlyList<TemplateParameter>? parameters = null,
        IReadOnlyList<TemplateExpansionEntry>? expansion = null) =>
        new(
            Id: id,
            DisplayName: id,
            Description: "child",
            Category: "test",
            Parameters: parameters ?? Array.Empty<TemplateParameter>(),
            Expansion: expansion ?? Array.Empty<TemplateExpansionEntry>(),
            ExtendsTemplateId: extends);

    [Fact]
    public void Child_without_parameters_or_expansion_additions_resolves_to_a_copy_of_the_parent()
    {
        var parent = Parent();
        var child = Child("child", extends: "parent");

        var reg = new TemplateRegistry(new[] { parent, child });
        var resolved = reg.Find("child")!;

        Assert.Equal(parent.Parameters.Count, resolved.Parameters.Count);
        Assert.Equal(parent.Expansion.Count, resolved.Expansion.Count);
        Assert.Null(resolved.ExtendsTemplateId);
        Assert.Equal("main", resolved.Expansion[0].Suffix);
    }

    [Fact]
    public void Child_adds_new_parameter_resolved_template_has_parents_params_plus_childs_new_one()
    {
        var parent = Parent();
        var child = Child("child", extends: "parent",
            parameters: new[] { new TemplateParameter("burst", "int", 12, "Burst budget.") });

        var reg = new TemplateRegistry(new[] { parent, child });
        var resolved = reg.Find("child")!;

        Assert.Equal(2, resolved.Parameters.Count);
        Assert.Contains(resolved.Parameters, p => string.Equals(p.Name, "rpm", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resolved.Parameters, p => string.Equals(p.Name, "burst", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Child_overrides_parent_parameter_default_child_default_wins()
    {
        var parent = Parent();
        var child = Child("child", extends: "parent",
            parameters: new[] { new TemplateParameter("rpm", "int", 99, "child override") });

        var reg = new TemplateRegistry(new[] { parent, child });
        var resolved = reg.Find("child")!;

        var rpm = resolved.Parameters.Single(p => string.Equals(p.Name, "rpm", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(99, rpm.Default);
        Assert.Equal("child override", rpm.Description);
        Assert.Single(resolved.Parameters);
    }

    [Fact]
    public void Child_overrides_parent_parameter_type_validation_error()
    {
        var parent = Parent(); // rpm: int
        var child = Child("child", extends: "parent",
            parameters: new[] { new TemplateParameter("rpm", "string", "fast") });

        var ex = Assert.Throws<InvalidOperationException>(
            () => new TemplateRegistry(new[] { parent, child }));
        Assert.Contains("child", ex.Message);
        Assert.Contains("rpm", ex.Message);
    }

    [Fact]
    public void Child_adds_expansion_additions_resolved_expansion_is_parents_plus_childs()
    {
        var parent = Parent();
        var child = Child("child", extends: "parent",
            expansion: new[]
            {
                new TemplateExpansionEntry("detect-mfa-bypass",
                    "fingerprint.integrity_score < 0.2",
                    "{ kind: challenge }",
                    Priority: 150,
                    Mode: "live")
            });

        var reg = new TemplateRegistry(new[] { parent, child });
        var resolved = reg.Find("child")!;

        Assert.Equal(2, resolved.Expansion.Count);
        Assert.Equal("main", resolved.Expansion[0].Suffix);
        Assert.Equal("detect-mfa-bypass", resolved.Expansion[1].Suffix);
    }

    [Fact]
    public void Child_reuses_a_parents_expansion_suffix_validation_error()
    {
        var parent = Parent();
        var child = Child("child", extends: "parent",
            expansion: new[]
            {
                // 'main' already exists on the parent.
                new TemplateExpansionEntry("main",
                    "ua.is_bot = true",
                    "{ kind: block }",
                    Priority: 50,
                    Mode: "live")
            });

        var ex = Assert.Throws<InvalidOperationException>(
            () => new TemplateRegistry(new[] { parent, child }));
        Assert.Contains("main", ex.Message);
        Assert.Contains("child", ex.Message);
    }

    [Fact]
    public void Cycle_A_extends_B_B_extends_A_validation_error_names_both_ids()
    {
        var a = Child("a", extends: "b", expansion: new[]
        {
            new TemplateExpansionEntry("a1", "ua.is_bot = true", "{ kind: block }", 100, "live")
        });
        var b = Child("b", extends: "a", expansion: new[]
        {
            new TemplateExpansionEntry("b1", "ua.is_bot = true", "{ kind: block }", 100, "live")
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => new TemplateRegistry(new[] { a, b }));
        Assert.Contains("a", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("b", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Self_extends_A_extends_A_validation_error()
    {
        var a = Child("a", extends: "a", expansion: new[]
        {
            new TemplateExpansionEntry("only", "ua.is_bot = true", "{ kind: block }", 100, "live")
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => new TemplateRegistry(new[] { a }));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_parent_validation_error_names_the_missing_id()
    {
        var child = Child("child", extends: "ghost", expansion: new[]
        {
            new TemplateExpansionEntry("only", "ua.is_bot = true", "{ kind: block }", 100, "live")
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => new TemplateRegistry(new[] { child }));
        Assert.Contains("ghost", ex.Message);
        Assert.Contains("child", ex.Message);
    }

    [Fact]
    public void Multi_level_chain_A_extends_B_extends_C_C_inherits_from_both()
    {
        var c = new Template(
            Id: "c-root",
            DisplayName: "c",
            Description: "root",
            Category: "test",
            Parameters: new[] { new TemplateParameter("rpm", "int", 6) },
            Expansion: new[]
            {
                new TemplateExpansionEntry("c-rule", "ua.is_bot = true", "{ kind: block }", 100, "live")
            });
        var b = new Template(
            Id: "b-mid",
            DisplayName: "b",
            Description: "mid",
            Category: "test",
            Parameters: new[] { new TemplateParameter("burst", "int", 12) },
            Expansion: new[]
            {
                new TemplateExpansionEntry("b-rule", "ua.is_bot = false", "{ kind: allow }", 200, "live")
            },
            ExtendsTemplateId: "c-root");
        var a = new Template(
            Id: "a-leaf",
            DisplayName: "a",
            Description: "leaf",
            Category: "test",
            Parameters: new[] { new TemplateParameter("threshold", "number", 0.5m) },
            Expansion: new[]
            {
                new TemplateExpansionEntry("a-rule", "fingerprint.integrity_score < {{ threshold }}", "{ kind: challenge }", 50, "live")
            },
            ExtendsTemplateId: "b-mid");

        var reg = new TemplateRegistry(new[] { a, b, c });
        var resolved = reg.Find("a-leaf")!;

        Assert.Equal(3, resolved.Parameters.Count);
        Assert.Equal(3, resolved.Expansion.Count);
        // Parent's expansion entries come first, then descendants in order.
        Assert.Equal("c-rule", resolved.Expansion[0].Suffix);
        Assert.Equal("b-rule", resolved.Expansion[1].Suffix);
        Assert.Equal("a-rule", resolved.Expansion[2].Suffix);
    }

    [Fact]
    public void Registry_Find_returns_fully_resolved_template_with_extends_nulled()
    {
        var parent = Parent();
        var child = Child("child", extends: "parent");

        var reg = new TemplateRegistry(new[] { parent, child });
        var resolved = reg.Find("child")!;

        Assert.Null(resolved.ExtendsTemplateId);
    }

    [Fact]
    public void TemplateResolver_works_against_an_extends_flattened_template_the_same_as_a_single_source_one()
    {
        var parent = Parent();
        var child = Child("child", extends: "parent",
            expansion: new[]
            {
                new TemplateExpansionEntry("block-bad",
                    "ua.is_bot = true",
                    "{ kind: block }",
                    Priority: 50,
                    Mode: "live")
            });

        var reg = new TemplateRegistry(new[] { parent, child });
        var resolved = reg.Find("child")!;

        var app = new TemplateApplication(
            Id: "app1",
            TemplateId: "child",
            AppliedTo: new[] { PolicyScope.Domain("acme.com") },
            Parameters: new Dictionary<string, object?>());

        var rules = new TemplateResolver().Resolve(resolved, app);

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.Origin!.ExpansionSuffix == "main");
        Assert.Contains(rules, r => r.Origin!.ExpansionSuffix == "block-bad");
        // The inherited 'main' entry uses the parent's default rpm of 6.
        var rateLimit = rules.Single(r => r.Origin!.ExpansionSuffix == "main");
        var rl = Assert.IsType<PolicyAction.RateLimit>(rateLimit.Action);
        Assert.Equal(6, rl.RequestsPerMinute);
    }

    [Fact]
    public void Child_override_of_default_flows_into_materialized_action()
    {
        var parent = Parent();
        var child = Child("child", extends: "parent",
            parameters: new[] { new TemplateParameter("rpm", "int", 33) });

        var reg = new TemplateRegistry(new[] { parent, child });
        var resolved = reg.Find("child")!;

        var app = new TemplateApplication(
            Id: "app1",
            TemplateId: "child",
            AppliedTo: new[] { PolicyScope.Wildcard() },
            Parameters: new Dictionary<string, object?>());

        var rules = new TemplateResolver().Resolve(resolved, app);

        var rl = Assert.IsType<PolicyAction.RateLimit>(rules.Single().Action);
        Assert.Equal(33, rl.RequestsPerMinute);
    }

    [Fact]
    public void Single_source_template_without_extends_is_returned_unchanged_by_registry()
    {
        var t = new Template(
            Id: "single",
            DisplayName: "Single source",
            Description: "no inheritance",
            Category: "test",
            Parameters: new[] { new TemplateParameter("rpm", "int", 6) },
            Expansion: new[]
            {
                new TemplateExpansionEntry("only", "ua.is_bot = true", "{ kind: block }", 100, "live")
            });

        var reg = new TemplateRegistry(new[] { t });
        var resolved = reg.Find("single")!;

        Assert.Null(resolved.ExtendsTemplateId);
        Assert.Single(resolved.Expansion);
        Assert.Single(resolved.Parameters);
    }
}
