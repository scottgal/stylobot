namespace Mostlylucid.BotDetection.UI.Models;

public sealed record TemplatePreviewViewModel(
    string TemplateId,
    string Scope,
    IReadOnlyList<TemplatePreviewRuleViewModel> AddedRules,
    IReadOnlyList<TemplatePreviewRuleViewModel> Conflicts,
    IReadOnlyList<TemplatePreviewRuleViewModel> Shadowed);

public sealed record TemplatePreviewRuleViewModel(
    int Priority,
    string ActionDisplay,
    string ModeDisplay,
    string PredicateText);
