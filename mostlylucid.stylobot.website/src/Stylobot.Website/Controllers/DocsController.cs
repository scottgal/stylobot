using Microsoft.AspNetCore.Mvc;
using Stylobot.Website.Services;

namespace Stylobot.Website.Controllers;

[Route("docs")]
public sealed class DocsController : Controller
{
    private readonly IMarkdownDocsService _docsService;
    private readonly SeoService _seoService;

    public DocsController(IMarkdownDocsService docsService, SeoService seoService)
    {
        _docsService = docsService;
        _seoService = seoService;
    }

    [HttpGet("")]
    [HttpGet("{slug}")]
    public IActionResult Index(string? slug)
    {
        var requestedSlug = string.IsNullOrWhiteSpace(slug) ? "start-here" : slug;

        if (!_docsService.TryGetPage(requestedSlug, out var page) || page is null)
        {
            return NotFound();
        }

        var metadata = _seoService.GetDefaultMetadata();
        metadata.Title = page.Title + " | Stylobot Docs";
        metadata.OgTitle = metadata.Title;
        metadata.TwitterTitle = metadata.Title;
        ViewData["SeoMetadata"] = metadata;

        return View(page);
    }
}
