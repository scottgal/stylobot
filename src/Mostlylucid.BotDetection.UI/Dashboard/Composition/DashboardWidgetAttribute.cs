namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DashboardWidgetAttribute(string key, DatasetKind needs) : Attribute
{
    public string Key { get; } = key;
    public DatasetKind Needs { get; } = needs;
}
