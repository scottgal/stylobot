using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Sample.Models;

namespace Mostlylucid.BotDetection.Sample.Controllers;

public class HomeController(ProductCatalog catalog) : Controller
{
    public IActionResult Index(string? category)
    {
        ViewBag.Categories       = catalog.Categories;
        ViewBag.SelectedCategory = category;
        return View(catalog.GetByCategory(category));
    }
}
