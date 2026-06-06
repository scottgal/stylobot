namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Default <see cref="IDashboardRowRegistry" /> backed by the static
///     FOSS group registry plus the <see cref="IDashboardPack" /> singletons
///     registered in DI.
/// </summary>
public sealed class DashboardRowRegistry : IDashboardRowRegistry
{
    private readonly Dictionary<string, DashboardRowMatch> _lookup;

    public DashboardRowRegistry(IEnumerable<IDashboardPack> packs)
    {
        Groups = FossDashboardGroups.All;
        Packs = packs.ToList();

        _lookup = new(StringComparer.OrdinalIgnoreCase);

        foreach (var group in Groups)
        foreach (var row in group.Rows)
        {
            _lookup[row.Id] = new DashboardRowMatch(
                Ref: new DashboardRowRef(row.Id),
                PartialPath: row.PartialPath,
                ViewComponentName: null,
                Pack: null,
                IsCommercialOnly: row.IsCommercialOnly);
        }

        foreach (var legacy in FossDashboardGroups.LegacyHidden)
        {
            _lookup[legacy.Id] = new DashboardRowMatch(
                Ref: new DashboardRowRef(legacy.Id),
                PartialPath: legacy.PartialPath,
                ViewComponentName: null,
                Pack: null,
                IsCommercialOnly: legacy.IsCommercialOnly);
        }

        foreach (var pack in Packs)
        {
            // Bare pack id 301s to the pack's first sub-row in the middleware,
            // so we do NOT register a bare entry here. The middleware checks
            // Packs by id before falling back to Resolve.
            foreach (var sub in pack.SubRows)
            {
                _lookup[$"{pack.Id}/{sub.Id}"] = new DashboardRowMatch(
                    Ref: new DashboardRowRef(pack.Id, sub.Id),
                    PartialPath: null,
                    ViewComponentName: sub.ViewComponentName,
                    Pack: pack,
                    IsCommercialOnly: false);
            }
        }
    }

    public IReadOnlyList<DashboardGroup> Groups { get; }
    public IReadOnlyList<IDashboardPack> Packs { get; }

    public DashboardRowMatch? Resolve(string area, string? sub)
    {
        var key = sub is null ? area : $"{area}/{sub}";
        return _lookup.GetValueOrDefault(key);
    }
}
