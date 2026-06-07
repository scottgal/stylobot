using FluentAssertions;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Mostlylucid.BotDetection.AspNetPack.Inventory;
using Mostlylucid.BotDetection.AspNetPack.Ui;

namespace Mostlylucid.BotDetection.Test.AspNetPack;

public class SbAspNetEndpointsViewComponentTests
{
    [Fact]
    public void Invoke_returns_view_with_inventory_snapshot_as_model()
    {
        var sample = new List<AspNetEndpointDescriptor>
        {
            new("GET", "/a", "A", "Home", "A", Array.Empty<string>()),
            new("POST", "/b", "B", "Home", "B", new[] { "DashboardWritePolicy" }),
        };
        var inventory = new Mock<IAspNetEndpointInventory>();
        inventory.Setup(i => i.All()).Returns(sample);

        var vc = new SbAspNetEndpointsViewComponent(inventory.Object);

        var result = vc.Invoke();

        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        var model = view.ViewData!.Model.Should().BeOfType<AspNetEndpointsViewModel>().Subject;
        model.Endpoints.Should().HaveCount(2);
        model.Endpoints[1].Authorize.Should().ContainSingle().Which.Should().Be("DashboardWritePolicy");
        inventory.Verify(i => i.All(), Times.Once);
    }

    [Fact]
    public void Invoke_with_empty_inventory_returns_view_with_empty_model()
    {
        var inventory = new Mock<IAspNetEndpointInventory>();
        inventory.Setup(i => i.All()).Returns(Array.Empty<AspNetEndpointDescriptor>());

        var vc = new SbAspNetEndpointsViewComponent(inventory.Object);

        var result = vc.Invoke();

        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        var model = view.ViewData!.Model.Should().BeOfType<AspNetEndpointsViewModel>().Subject;
        model.Endpoints.Should().BeEmpty();
    }
}
