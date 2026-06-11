using Mostlylucid.BotDetection.Policies.Templates;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Read-side helper that buckets a flat <see cref="PolicyStackRowViewModel"/>
///     list under the template that emitted each rule. Used by the
///     <c>/dashboard/policies</c> view (T4 Part B) to render rules
///     grouped under their owning template + application headers, with a
///     trailing "Manually authored" group catching bare hand-authored
///     rules.
///
///     <para>
///         The grouping is pure: input is the rows from the presenter +
///         an optional <see cref="TemplateRegistry"/> for display-name
///         lookup. No DI, no I/O, no state -- callable from a Razor
///         partial when the dashboard is reading its own injected
///         services. Bucketing preserves the resolver order within each
///         group so the existing most-specific-first sort stays intact.
///     </para>
/// </summary>
public static class PolicyTemplateGrouping
{
    /// <summary>
    ///     Label rendered for the catch-all group covering rules whose
    ///     <see cref="PolicyStackRowViewModel.Origin"/> is <c>null</c>.
    /// </summary>
    public const string ManuallyAuthoredLabel = "Manually authored";

    /// <summary>
    ///     Group <paramref name="rows"/> by template id. Hand-authored
    ///     rules (origin null) collect into the trailing "Manually
    ///     authored" group. <paramref name="registry"/> is optional --
    ///     when supplied, group headers surface the template's
    ///     <c>DisplayName</c>; otherwise the bare template id is used.
    ///
    ///     <para>
    ///         Returns an empty list when <paramref name="rows"/> is
    ///         empty so the caller can short-circuit the empty-state path
    ///         without checking inside each group.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<PolicyStackTemplateGroupViewModel> Group(
        IReadOnlyList<PolicyStackRowViewModel> rows,
        TemplateRegistry? registry = null)
    {
        if (rows.Count == 0) return Array.Empty<PolicyStackTemplateGroupViewModel>();

        var templateBuckets = new Dictionary<string, List<PolicyStackRowViewModel>>(StringComparer.Ordinal);
        var templateApps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var manual = new List<PolicyStackRowViewModel>();

        foreach (var row in rows)
        {
            if (row.Origin is null)
            {
                manual.Add(row);
                continue;
            }
            if (!templateBuckets.TryGetValue(row.Origin.TemplateId, out var bucket))
            {
                bucket = new List<PolicyStackRowViewModel>();
                templateBuckets[row.Origin.TemplateId] = bucket;
            }
            bucket.Add(row);
            if (!templateApps.TryGetValue(row.Origin.TemplateId, out var apps))
            {
                apps = new HashSet<string>(StringComparer.Ordinal);
                templateApps[row.Origin.TemplateId] = apps;
            }
            apps.Add(row.Origin.ApplicationId);
        }

        var groups = new List<PolicyStackTemplateGroupViewModel>(templateBuckets.Count + 1);

        // Template-emitted groups, sorted by display name for a stable
        // operator-facing order. Within each group, rules keep the
        // resolver's most-specific-first order so the operator scans the
        // group top-down the same way they scan the flat list.
        foreach (var (templateId, bucket) in templateBuckets.OrderBy(kv => Display(kv.Key, registry), StringComparer.OrdinalIgnoreCase))
        {
            var apps = templateApps[templateId].OrderBy(static a => a, StringComparer.Ordinal).ToList();
            var diverged = 0;
            foreach (var r in bucket)
            {
                if (r.IsDiverged) diverged++;
            }
            groups.Add(new PolicyStackTemplateGroupViewModel(
                TemplateId: templateId,
                TemplateDisplayName: Display(templateId, registry),
                ApplicationIds: apps,
                Rows: bucket,
                IsManuallyAuthored: false,
                DivergedCount: diverged));
        }

        // Manually authored group always trails so template-managed
        // groups read top-down first.
        if (manual.Count > 0)
        {
            groups.Add(new PolicyStackTemplateGroupViewModel(
                TemplateId: string.Empty,
                TemplateDisplayName: ManuallyAuthoredLabel,
                ApplicationIds: Array.Empty<string>(),
                Rows: manual,
                IsManuallyAuthored: true,
                DivergedCount: 0));
        }

        return groups;
    }

    private static string Display(string templateId, TemplateRegistry? registry)
    {
        if (registry is null) return templateId;
        var t = registry.Find(templateId);
        return t?.DisplayName ?? templateId;
    }
}
