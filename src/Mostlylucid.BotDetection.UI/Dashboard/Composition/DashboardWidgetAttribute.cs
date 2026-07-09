namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Declares which composed dataset a dashboard widget needs. A widget either
///     needs a FOSS <see cref="DatasetKind"/> (the batched detection aggregates) OR a
///     pack/commercial extension kind (a string name resolved by an
///     <see cref="IDashboardDatasetExtension"/>) — never both.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DashboardWidgetAttribute : Attribute
{
    public string Key { get; }

    /// <summary>The FOSS batched dataset this widget needs; null for extension-backed widgets.</summary>
    public DatasetKind? Needs { get; }

    /// <summary>The pack/commercial extension kind this widget needs; null for FOSS-dataset widgets.</summary>
    public string? ExtensionKind { get; }

    /// <summary>A widget backed by a FOSS batched <see cref="DatasetKind"/>.</summary>
    public DashboardWidgetAttribute(string key, DatasetKind needs)
    {
        Key = key;
        Needs = needs;
        ExtensionKind = null;
    }

    /// <summary>A widget backed by a pack/commercial <see cref="IDashboardDatasetExtension"/> kind.</summary>
    public DashboardWidgetAttribute(string key, string extensionKind)
    {
        Key = key;
        Needs = null;
        ExtensionKind = extensionKind;
    }
}
