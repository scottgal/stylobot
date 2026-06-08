namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Top-level view model for the SbPolicyStack explainer panel. Built by
///     <see cref="Services.PolicyExplainerPresenter"/> when the SbPolicyStack
///     presenter is invoked with a fingerprint; <c>null</c> on the parent
///     <see cref="PolicyStackViewModel"/> when no fingerprint has been picked.
/// </summary>
/// <param name="FingerprintId">Fingerprint the explainer is replaying. Empty string when the input picker has been shown but no fingerprint was entered.</param>
/// <param name="ReplayedAt">When the original decision cycle was observed. <c>null</c> when no decision rows were found.</param>
/// <param name="Rules">Per-rule trace rows in priority order, mirroring the Effective tab.</param>
/// <param name="WinnerRuleId">Rule that decided the cycle. <c>null</c> when no cycle was found.</param>
/// <param name="ContextSummary">Short pre-rendered context line -- e.g. <c>GET /api/upload (fp-1234)</c>.</param>
/// <param name="Locked"><c>true</c> when the picker is hidden (fingerprint detail pages embed locked explainers).</param>
public sealed record PolicyExplainerViewModel(
    string FingerprintId,
    DateTimeOffset? ReplayedAt,
    IReadOnlyList<PolicyExplainerRuleViewModel> Rules,
    Guid? WinnerRuleId,
    string ContextSummary,
    bool Locked);
