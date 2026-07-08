namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;
using System.Reflection;
using Mostlylucid.BotDetection.UI.Models;

public sealed class DashboardWidgetCatalog
{
    private readonly IReadOnlyDictionary<string, DatasetKind> _widgets;

    private DashboardWidgetCatalog(IReadOnlyDictionary<string, DatasetKind> widgets) => _widgets = widgets;

    public IReadOnlyDictionary<string, DatasetKind> Widgets => _widgets;

    public DatasetKind? NeedsFor(string key) => _widgets.TryGetValue(key, out var k) ? k : null;

    public static DashboardWidgetCatalog BuildFrom(IEnumerable<Type> types)
    {
        var map = new Dictionary<string, DatasetKind>(StringComparer.Ordinal);
        foreach (var t in types)
        {
            var attr = t.GetCustomAttribute<DashboardWidgetAttribute>();
            if (attr is not null) map[attr.Key] = attr.Needs;
        }
        return new DashboardWidgetCatalog(map);
    }

    public static DashboardWidgetCatalog BuildFromLoadedAssemblies() =>
        BuildFrom(AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .Where(t => t.GetCustomAttribute<DashboardWidgetAttribute>() is not null));
}
