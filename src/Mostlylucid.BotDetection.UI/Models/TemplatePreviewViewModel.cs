namespace Mostlylucid.BotDetection.UI.Models;

public sealed record TemplatePreviewViewModel(
    string TemplateId,
    string Scope,
    IReadOnlyList<TemplatePreviewRuleViewModel> AddedRules,
    IReadOnlyList<TemplatePreviewConflictViewModel> Conflicts,
    IReadOnlyList<TemplatePreviewRuleViewModel> Shadowed);

public sealed record TemplatePreviewRuleViewModel(
    int Priority,
    string ActionDisplay,
    string ModeDisplay,
    string PredicateText);

public sealed record TemplatePreviewConflictViewModel(
    TemplatePreviewRuleViewModel TemplateRule,
    TemplatePreviewRuleViewModel ExistingRule);
