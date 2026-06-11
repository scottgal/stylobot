using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     One row in the SbPolicyStack Effective tab. Every signal the row needs
///     is pre-computed by <see cref="Services.PolicyStackPresenter"/> so the
///     Razor partial stays declarative: pick attributes, write spans, done.
/// </summary>
/// <param name="RuleId">Stable rule identifier; the row's <c>data-rule-id</c>.</param>
/// <param name="SourceScope">Where the rule is authored. Distinct from the row's queried scope.</param>
/// <param name="IsInherited">Row inherited from a broader scope than the one we queried.</param>
/// <param name="SourcePill">Uppercase pill text -- <c>ENDPOINT</c>/<c>SUBDOMAIN</c>/<c>DOMAIN</c>/<c>GLOBAL</c>.</param>
/// <param name="Mode">Lifecycle mode the rule is in.</param>
/// <param name="ModePill">Uppercase pill text -- <c>LIVE</c>/<c>OBSERVE</c>/<c>DRAFT</c>/<c>DISABLED</c>.</param>
/// <param name="ActionVerdict">Human-readable verdict text -- <c>"Block"</c>/<c>"Challenge (turnstile)"</c> etc.</param>
/// <param name="ActionColorClass">Canonical dashboard verdict colour class -- <c>verdict-error</c>/<c>verdict-warning</c>/<c>verdict-info</c>/<c>verdict-success</c>.</param>
/// <param name="PredicateText">Round-tripped predicate text from <see cref="Policies.Predicate.PredicateFormatter.Format"/>.</param>
/// <param name="Chips">Flat list of predicate chip rows; AND/OR structure baked in.</param>
/// <param name="TriggerCount">Matched-in-window count from the aggregate.</param>
/// <param name="TotalEvaluations">Total evaluations in the window for this rule.</param>
/// <param name="WinDistribution">Lowercase action-kind to win count, from the aggregate.</param>
/// <param name="DistributionLine">Pre-rendered distribution line -- empty when no evaluations.</param>
/// <param name="P50LatencyMicros">Median predicate-eval latency, microseconds.</param>
/// <param name="P99LatencyMicros">99th-percentile predicate-eval latency, microseconds.</param>
/// <param name="LatencyLine">Pre-rendered "p50 ... -- p99 ..." string; empty when no samples.</param>
/// <param name="CreatedAt">When the rule was first loaded into this process.</param>
/// <param name="MetadataLine">Pre-rendered metadata text -- <c>"default"</c> or <c>"edited Nd ago"</c>.</param>
/// <param name="IsObserveMode">True when the rule's mode is Observe -- prepends "would " to verdict.</param>
/// <param name="HasNoHits">True when the row has not triggered across a 7d+ window.</param>
/// <param name="IsModified">True when the rule's source is NOT an <c>embedded:</c> seed -- powers the <c>@modified</c> filter.</param>
/// <param name="LastEditedAt">When the rule was last edited; equal to <paramref name="CreatedAt"/> for default rules. Powers the <c>@since:</c> filter + the <see cref="PolicyStackSortKey.LastEdited"/> sort.</param>
/// <param name="ActionKind">Lowercase canonical action kind (<c>"block"</c>, <c>"challenge"</c>, ...) for the <c>@action:</c> filter.</param>
/// <param name="ScopeKind">Lowercase canonical source-scope kind (<c>"endpoint"</c>, <c>"subdomain"</c>, <c>"domain"</c>, <c>"wildcard"</c>) for the <c>@scope:</c> filter.</param>
/// <param name="CanEdit">When <c>true</c>, the row renders the C6 pencil button that swaps the row in for the <c>_EditRow</c> partial. Threaded from the call-site canEdit flag, which itself is gated on the operator's <c>dashboard-write</c> role.</param>
/// <param name="Origin">
///     Template-provenance stamp -- <c>null</c> for hand-authored rules.
///     Powers the T4 dashboard "rules grouped by template + application"
///     view. Threaded straight from <see cref="PolicyRule.Origin"/>.
/// </param>
/// <param name="IsDiverged">
///     <c>true</c> when the materialized rule has been edited away from
///     the template's last materialization (T2 override-divergence
///     detection). Drives the inline "diverged from template" banner on
///     <c>_RuleRow.cshtml</c>. Always <c>false</c> for hand-authored
///     rules and on stores that do not perform divergence tracking.
/// </param>
public sealed record PolicyStackRowViewModel(
    Guid RuleId,
    PolicyScope SourceScope,
    bool IsInherited,
    string SourcePill,
    PolicyMode Mode,
    string ModePill,
    string ActionVerdict,
    string ActionColorClass,
    string PredicateText,
    IReadOnlyList<PolicyStackChipViewModel> Chips,
    int TriggerCount,
    int TotalEvaluations,
    IReadOnlyDictionary<string, int> WinDistribution,
    string DistributionLine,
    long P50LatencyMicros,
    long P99LatencyMicros,
    string LatencyLine,
    DateTimeOffset CreatedAt,
    string MetadataLine,
    bool IsObserveMode,
    bool HasNoHits,
    bool IsModified,
    DateTimeOffset LastEditedAt,
    string ActionKind,
    string ScopeKind,
    bool CanEdit = false,
    RuleOrigin? Origin = null,
    bool IsDiverged = false);

/// <summary>
///     One predicate chip on the compact rule row. Either a real term
///     (<see cref="Facet"/> + <see cref="Op"/> + <see cref="ValueText"/> with
///     a SignalCatalog tooltip) or a structural marker
///     (<see cref="IsStructural"/> + <see cref="Facet"/> carries the keyword
///     -- <c>"OR"</c>, <c>"("</c>, <c>")"</c>).
/// </summary>
/// <param name="Facet">Signal key for a term chip, or the keyword for a structural chip.</param>
/// <param name="Op">Canonical operator text. Empty for structural chips.</param>
/// <param name="ValueText">Pre-rendered value -- <c>"(scraper, crawler)"</c>, <c>"0.7"</c>, etc. Empty for structural chips.</param>
/// <param name="Tooltip">SignalCatalog short description for the facet; empty when unknown or structural.</param>
/// <param name="IsStructural">True when this chip is a layout marker (OR/parens) not a real term.</param>
public sealed record PolicyStackChipViewModel(
    string Facet,
    string Op,
    string ValueText,
    string Tooltip,
    bool IsStructural = false);
