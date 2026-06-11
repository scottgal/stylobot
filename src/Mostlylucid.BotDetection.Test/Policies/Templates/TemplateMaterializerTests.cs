using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.Test.Policies.Templates;

/// <summary>
///     Pin <see cref="TemplateMaterializer"/> for T2: deterministic
///     materialization, missing-template error reporting, and override-
///     divergence detection across reloads.
/// </summary>
public class TemplateMaterializerTests
{
    private static Template TwoEntryTemplate(string id = "t-two") => new(
        Id: id,
        DisplayName: id,
        Description: "two",
        Category: "test",
        Parameters: Array.Empty<TemplateParameter>(),
        Expansion: new[]
        {
            new TemplateExpansionEntry("a", "ua.is_bot = true",  "{ kind: block }", 100, "live"),
            new TemplateExpansionEntry("b", "ua.is_bot = false", "{ kind: allow }", 200, "live"),
        });

    private static TemplateApplication App(string id, string templateId, params PolicyScope[] scopes) =>
        new(Id: id,
            TemplateId: templateId,
            AppliedTo: scopes,
            Parameters: new Dictionary<string, object?>());

    private static TemplateMaterializer MakeMaterializer(params Template[] templates)
    {
        var registry = new TemplateRegistry(templates);
        return new TemplateMaterializer(registry, new TemplateResolver());
    }

    [Fact]
    public void Single_application_produces_N_materialized_rules_with_origins()
    {
        var t = TwoEntryTemplate();
        var sut = MakeMaterializer(t);

        var result = sut.Materialize(new[] { App("app1", t.Id, PolicyScope.Domain("acme.com")) });

        // 2 expansion entries x 1 applied-to scope.
        Assert.Equal(2, result.Rules.Count);
        Assert.All(result.Rules, r =>
        {
            Assert.NotNull(r.Origin);
            Assert.Equal(t.Id, r.Origin!.TemplateId);
            Assert.Equal("app1", r.Origin.ApplicationId);
        });
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.RuleHashes.Count);
    }

    [Fact]
    public void Missing_template_is_reported_in_errors_other_apps_still_process()
    {
        var t = TwoEntryTemplate();
        var sut = MakeMaterializer(t);

        var apps = new[]
        {
            App("good", t.Id, PolicyScope.Domain("acme.com")),
            App("orphan", "does-not-exist", PolicyScope.Domain("acme.com")),
        };

        var result = sut.Materialize(apps);

        Assert.Equal(2, result.Rules.Count); // only "good" produced rules
        Assert.Single(result.Errors);
        Assert.Equal("orphan", result.Errors[0].ApplicationId);
        Assert.Contains("does-not-exist", result.Errors[0].Message);
    }

    [Fact]
    public void Materialization_is_deterministic_same_inputs_same_rule_ids_same_hashes()
    {
        var t = TwoEntryTemplate();
        var sut = MakeMaterializer(t);
        var apps = new[] { App("appx", t.Id, PolicyScope.Domain("acme.com")) };

        var first = sut.Materialize(apps);
        var second = sut.Materialize(apps);

        var firstIds = first.Rules.Select(r => r.Id).ToArray();
        var secondIds = second.Rules.Select(r => r.Id).ToArray();
        Assert.Equal(firstIds, secondIds);

        // Per-rule hash equality across runs -- the hasher must NOT depend on
        // any field that changes between materializations (CreatedAt, RevisionId).
        foreach (var id in firstIds)
            Assert.Equal(first.RuleHashes[id], second.RuleHashes[id]);
    }

    [Fact]
    public void Override_divergence_preserves_edited_materialized_rule()
    {
        var t = TwoEntryTemplate();
        var sut = MakeMaterializer(t);
        var apps = new[] { App("appz", t.Id, PolicyScope.Domain("acme.com")) };

        // First materialization to populate prior hashes.
        var first = sut.Materialize(apps);
        var firstHashes = first.RuleHashes;

        // Operator manually edits the FIRST materialized rule -- change its
        // priority. This is the per-process "operator opened the rule editor
        // and saved" path.
        var edited = first.Rules[0] with { Priority = 999 };

        var existing = new Dictionary<Guid, PolicyRule>
        {
            [edited.Id] = edited,
            // Second rule unchanged.
            [first.Rules[1].Id] = first.Rules[1]
        };

        var second = sut.MaterializeWithDivergence(apps, existing, firstHashes);

        Assert.Equal(2, second.Rules.Count);
        // Edited rule preserved -- priority 999, NOT the template's 100.
        var firstAfter = second.Rules.Single(r => r.Id == edited.Id);
        Assert.Equal(999, firstAfter.Priority);
        // The unchanged rule re-materialized cleanly with template priority.
        var secondAfter = second.Rules.Single(r => r.Id == first.Rules[1].Id);
        Assert.Equal(200, secondAfter.Priority);
    }

    [Fact]
    public void Override_divergence_flags_edited_rule_in_DivergedRuleIds()
    {
        var t = TwoEntryTemplate();
        var sut = MakeMaterializer(t);
        var apps = new[] { App("app-d", t.Id, PolicyScope.Domain("acme.com")) };

        var first = sut.Materialize(apps);
        var edited = first.Rules[0] with { Priority = 7 };

        var existing = new Dictionary<Guid, PolicyRule>
        {
            [edited.Id] = edited,
            [first.Rules[1].Id] = first.Rules[1]
        };

        var second = sut.MaterializeWithDivergence(apps, existing, first.RuleHashes);

        Assert.NotNull(second.DivergedRuleIds);
        Assert.Contains(edited.Id, second.DivergedRuleIds!);
        Assert.DoesNotContain(first.Rules[1].Id, second.DivergedRuleIds!);
    }

    [Fact]
    public void Untouched_materialized_rules_are_replaced_wholesale_by_re_materialization()
    {
        var t = TwoEntryTemplate();
        var sut = MakeMaterializer(t);
        var apps = new[] { App("apprm", t.Id, PolicyScope.Domain("acme.com")) };

        var first = sut.Materialize(apps);

        // Existing carries the rules with the SAME hashes as the prior
        // materialization -- nothing was edited.
        var existing = first.Rules.ToDictionary(r => r.Id);

        var second = sut.MaterializeWithDivergence(apps, existing, first.RuleHashes);

        Assert.NotNull(second.DivergedRuleIds);
        Assert.Empty(second.DivergedRuleIds!);
        // Rule ids match (deterministic), and these are the FRESH ones (new
        // RevisionId stamped on each, but Id and structural fields equal).
        Assert.Equal(first.Rules.Select(r => r.Id).OrderBy(g => g),
                     second.Rules.Select(r => r.Id).OrderBy(g => g));
    }

    [Fact]
    public void Identical_rematerialization_does_not_introduce_false_positive_divergence()
    {
        // The constraint from the spec: "a rule that produces the same hash
        // after re-materialize is NOT diverged". Even if `existing` contains
        // the rule, no divergence should be raised because the hash matches.
        var t = TwoEntryTemplate();
        var sut = MakeMaterializer(t);
        var apps = new[] { App("idem", t.Id, PolicyScope.Domain("acme.com")) };

        var first = sut.Materialize(apps);

        // Existing rules are byte-identical to fresh.
        var existing = first.Rules.ToDictionary(r => r.Id);
        var second = sut.MaterializeWithDivergence(apps, existing, first.RuleHashes);

        Assert.Empty(second.DivergedRuleIds!);
    }

    [Fact]
    public void Materialize_with_empty_applications_returns_empty_corpus()
    {
        var sut = MakeMaterializer(TwoEntryTemplate());

        var result = sut.Materialize(Array.Empty<TemplateApplication>());

        Assert.Empty(result.Rules);
        Assert.Empty(result.Errors);
        Assert.Empty(result.RuleHashes);
    }
}
