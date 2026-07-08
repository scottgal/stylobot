using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.Dashboard;

public sealed class DashboardWidgetCatalogTests
{
    [DashboardWidget("test-topbots", DatasetKind.BotAggregate)]
    private sealed class FakeWidget { }

    [Fact]
    public void Catalog_discovers_attributed_widget()
    {
        var catalog = DashboardWidgetCatalog.BuildFrom(new[] { typeof(FakeWidget) });
        Assert.Equal(DatasetKind.BotAggregate, catalog.NeedsFor("test-topbots"));
    }
}