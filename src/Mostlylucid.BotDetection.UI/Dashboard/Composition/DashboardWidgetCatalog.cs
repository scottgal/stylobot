namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;
using System.Reflection;
using Mostlylucid.BotDetection.UI.Models;

public sealed class DashboardWidgetCatalog
{
    private readonly IReadOnlyDictionary<string, DatasetKind> _widgets;
    private readonly IReadOnlyDictionary<string, string> _extensionWidgets;

    private DashboardWidgetCatalog(
        IReadOnlyDictionary<string, DatasetKind> widgets,
        IReadOnlyDictionary<string, string> extensionWidgets)
    {
        _widgets = widgets;
        _extensionWidgets = extensionWidgets;
    }

    public IReadOnlyDictionary<string, DatasetKind> Widgets => _widgets;

    /// <summary>The FOSS batched <see cref="DatasetKind"/> a widget key needs; null if unknown or extension-backed.</summary>
    public DatasetKind? NeedsFor(string key) => _widgets.TryGetValue(key, out var k) ? k : null;

    /// <summary>The pack/commercial extension kind a widget key needs; null if unknown or FOSS-dataset-backed.</summary>
    public string? ExtensionKindFor(string key) => _extensionWidgets.TryGetValue(key, out var s) ? s : null;

    public static DashboardWidgetCatalog BuildFrom(IEnumerable<Type> types)
    {
        var map = new Dictionary<string, DatasetKind>(StringComparer.Ordinal);
        var extMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in types)
        {
            var attr = t.GetCustomAttribute<DashboardWidgetAttribute>();
            if (attr is null) continue;
            if (attr.Needs is { } kind) map[attr.Key] = kind;
            else if (attr.ExtensionKind is { } ext) extMap[attr.Key] = ext;
        }
        return new DashboardWidgetCatalog(map, extMap);
    }

    public static DashboardWidgetCatalog BuildFromLoadedAssemblies() =>
        BuildFrom(AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .Where(t => t.GetCustomAttribute<DashboardWidgetAttribute>() is not null));
}
