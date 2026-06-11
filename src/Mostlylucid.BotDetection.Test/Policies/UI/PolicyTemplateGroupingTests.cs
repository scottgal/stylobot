using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     T4 Part B coverage: pure-function bucketing of effective rule rows
///     into template-keyed groups, with hand-authored rules collected
///     into a trailing "Manually authored" group.
/// </summary>
public sealed class PolicyTemplateGroupingTests
{
    [Fact]
    public void Group_empty_input_returns_empty()
    {
        var result = PolicyTemplateGrouping.Group(Array.Empty<PolicyStackRowViewModel>());
        Assert.Empty(result);
    }

    [Fact]
    public void Group_buckets_by_template_id()
    {
        var rows = new[]
        {
            BuildRow(templateId: "block-overload", applicationId: "ovl-1"),
            BuildRow(templateId: "block-overload", applicationId: "ovl-1"),
            BuildRow(templateId: "verified-bots", applicationId: "vb-1"),
        };

        var groups = PolicyTemplateGrouping.Group(rows);

        // Two template-keyed groups, no manual-authored group.
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsManuallyAuthored));

        var overload = groups.Single(g => g.TemplateId == "block-overload");
        Assert.Equal(2, overload.Rows.Count);
        Assert.Equal(new[] { "ovl-1" }, overload.ApplicationIds);

        var verified = groups.Single(g => g.TemplateId == "verified-bots");
        Assert.Single(verified.Rows);
    }

    [Fact]
    public void Group_collects_hand_authored_rules_into_trailing_group()
    {
        var rows = new[]
        {
            BuildRow(templateId: "block-overload", applicationId: "ovl-1"),
            BuildRow(templateId: null,             applicationId: null),
            BuildRow(templateId: null,             applicationId: null),
        };

        var groups = PolicyTemplateGrouping.Group(rows);

        Assert.Equal(2, groups.Count);
        // Manual group sits LAST so template-managed work reads top-down first.
        Assert.True(groups[^1].IsManuallyAuthored);
        Assert.Equal(2, groups[^1].Rows.Count);
        Assert.Equal(PolicyTemplateGrouping.ManuallyAuthoredLabel, groups[^1].TemplateDisplayName);
    }

    [Fact]
    public void Group_dedupes_application_ids_within_a_template_group()
    {
        var rows = new[]
        {
            BuildRow(templateId: "block-overload", applicationId: "ovl-1"),
            BuildRow(templateId: "block-overload", applicationId: "ovl-1"),
            BuildRow(templateId: "block-overload", applicationId: "ovl-2"),
        };

        var groups = PolicyTemplateGrouping.Group(rows);

        var g = Assert.Single(groups);
        Assert.Equal(new[] { "ovl-1", "ovl-2" }, g.ApplicationIds);
    }

    [Fact]
    public void Group_uses_registry_display_name_when_available()
    {
        var template = new Template(
            Id: "block-overload",
            DisplayName: "Block during overload",
            Description: "",
            Category: "overload",
            Parameters: Array.Empty<TemplateParameter>(),
            Expansion: Array.Empty<TemplateExpansionEntry>());
        var registry = new TemplateRegistry(new[] { template });

        var rows = new[] { BuildRow(templateId: "block-overload", applicationId: "ovl-1") };
        var groups = PolicyTemplateGrouping.Group(rows, registry);

        Assert.Equal("Block during overload", groups[0].TemplateDisplayName);
    }

    [Fact]
    public void Group_falls_back_to_template_id_when_registry_missing()
    {
        var rows = new[] { BuildRow(templateId: "unknown-template", applicationId: "app-1") };
        var groups = PolicyTemplateGrouping.Group(rows, registry: null);

        Assert.Equal("unknown-template", groups[0].TemplateDisplayName);
    }

    [Fact]
    public void Group_counts_diverged_rows_per_group()
    {
        var rows = new[]
        {
            BuildRow(templateId: "tpl", applicationId: "app", isDiverged: true),
            BuildRow(templateId: "tpl", applicationId: "app", isDiverged: false),
            BuildRow(templateId: "tpl", applicationId: "app", isDiverged: true),
        };

        var groups = PolicyTemplateGrouping.Group(rows);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].DivergedCount);
    }

    [Fact]
    public void Group_preserves_row_order_within_a_group()
    {
        var first = BuildRow(templateId: "tpl", applicationId: "app", ruleId: Guid.NewGuid());
        var second = BuildRow(templateId: "tpl", applicationId: "app", ruleId: Guid.NewGuid());
        var third = BuildRow(templateId: "tpl", applicationId: "app", ruleId: Guid.NewGuid());

        var groups = PolicyTemplateGrouping.Group(new[] { first, second, third });
        Assert.Same(first, groups[0].Rows[0]);
        Assert.Same(second, groups[0].Rows[1]);
        Assert.Same(third, groups[0].Rows[2]);
    }

    // -------- Test row helper ----------------------------------------

    private static PolicyStackRowViewModel BuildRow(
        string? templateId = null,
        string? applicationId = null,
        bool isDiverged = false,
        Guid? ruleId = null)
    {
        RuleOrigin? origin = templateId is not null && applicationId is not null
            ? new RuleOrigin(templateId, applicationId, "default")
            : null;
        return new PolicyStackRowViewModel(
            RuleId: ruleId ?? Guid.NewGuid(),
            SourceScope: PolicyScope.Wildcard(),
            IsInherited: false,
            SourcePill: "GLOBAL",
            Mode: PolicyMode.Live,
            ModePill: "LIVE",
            ActionVerdict: "Block",
            ActionColorClass: "verdict-error",
            PredicateText: "true",
            Chips: Array.Empty<PolicyStackChipViewModel>(),
            TriggerCount: 0,
            TotalEvaluations: 0,
            WinDistribution: new Dictionary<string, int>(),
            DistributionLine: string.Empty,
            P50LatencyMicros: 0,
            P99LatencyMicros: 0,
            LatencyLine: string.Empty,
            CreatedAt: DateTimeOffset.UtcNow,
            MetadataLine: string.Empty,
            IsObserveMode: false,
            HasNoHits: false,
            IsModified: false,
            LastEditedAt: DateTimeOffset.UtcNow,
            ActionKind: "block",
            ScopeKind: "wildcard",
            CanEdit: false,
            Origin: origin,
            IsDiverged: isDiverged);
    }
}
