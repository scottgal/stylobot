using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Sample.Models;

namespace Mostlylucid.BotDetection.Sample.Controllers;

public class ProductController(ProductCatalog catalog) : Controller
{
    public IActionResult Detail(int id)
    {
        var product = catalog.GetById(id);
        if (product is null) return NotFound();
        return View(product);
    }
}
