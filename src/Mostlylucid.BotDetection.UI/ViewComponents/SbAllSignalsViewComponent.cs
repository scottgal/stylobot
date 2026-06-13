using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders every signal key/value pair from the current request's
///     blackboard, grouped by key prefix. Each row carries the signal-catalog
///     short description as a tooltip when the catalog knows the key.
///
///     Side benefit: the <c>headless.framework</c> signal that names the
///     specific automation framework (Puppeteer / Playwright / Selenium /
///     PhantomJS / CDP) is among the keys read here, so consumers that
///     embed this view component see the framework name surface
///     automatically even when the rest of the dashboard collapses it
///     into a family identity.
/// </summary>
public sealed class SbAllSignalsViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;
    private readonly ISignalCatalog? _catalog;

    public SbAllSignalsViewComponent(DetectionDataExtractor extractor, ISignalCatalog? catalog = null)
    {
        _extractor = extractor;
        _catalog = catalog;
    }

    /// <summary>
    ///     Invoke the view component.
    /// </summary>
    /// <param name="viewName">Razor view to render; defaults to <c>Default</c>.</param>
    /// <param name="cssClass">Extra css class applied to the outer container.</param>
    /// <param name="showDescriptions">
    ///     When true the catalog short description renders inline next to
    ///     each signal row instead of staying hidden in the hover tooltip.
    ///     Defaults to false so the dashboard's dense usage stays compact;
    ///     the conference demo on aspnet.stylobot.net passes true.
    /// </param>
    public IViewComponentResult Invoke(
        string viewName = "Default",
        string? cssClass = null,
        bool showDescriptions = false)
    {
        var detection = HttpContext != null ? _extractor.Extract(HttpContext) : new DetectionDisplayModel();
        var groups = BuildGroups(detection.RawSignals);

        var model = new AllSignalsViewModel(
            RiskBand: detection.RiskBand,
            BotProbability: detection.BotProbability,
            Groups: groups,
            TotalCount: detection.RawSignals.Count,
            CssClass: cssClass,
            ShowDescriptions: showDescriptions);

        return View(viewName, model);
    }

    private IReadOnlyList<SignalGroup> BuildGroups(IReadOnlyDictionary<string, object> rawSignals)
    {
        var grouped = rawSignals
            .Select(kvp =>
            {
                var key = kvp.Key ?? string.Empty;
                var dotIndex = key.IndexOf('.');
                var prefix = dotIndex > 0 ? key.Substring(0, dotIndex) : "other";
                var description = _catalog?.TryGet(key);
                return new
                {
                    Prefix = prefix,
                    Row = new SignalRow(
                        Key: key,
                        Value: FormatValue(kvp.Value),
                        Short: description?.Short ?? string.Empty,
                        Long: description?.Long ?? string.Empty,
                        SourceType: description?.DeclaringType ?? string.Empty)
                };
            })
            .GroupBy(x => x.Prefix, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SignalGroup(
                Prefix: g.Key,
                Rows: g.Select(x => x.Row).OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
        return grouped;
    }

    private static string FormatValue(object? value)
    {
        if (value is null) return "null";
        if (value is string s) return s;
        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var items = new List<string>();
            foreach (var item in enumerable) items.Add(item?.ToString() ?? "null");
            return "[" + string.Join(", ", items) + "]";
        }
        return value.ToString() ?? string.Empty;
    }
}

/// <summary>Top level view model the SbAllSignals view binds to.</summary>
public sealed record AllSignalsViewModel(
    string RiskBand,
    double BotProbability,
    IReadOnlyList<SignalGroup> Groups,
    int TotalCount,
    string? CssClass,
    bool ShowDescriptions);

/// <summary>Signals grouped by key namespace prefix (the segment before the first dot).</summary>
public sealed record SignalGroup(
    string Prefix,
    IReadOnlyList<SignalRow> Rows);

/// <summary>One signal row in the grouped table.</summary>
public sealed record SignalRow(
    string Key,
    string Value,
    string Short,
    string Long,
    string SourceType);
