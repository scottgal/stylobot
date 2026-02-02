using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace Stylobot.Website.Controllers;

public class SitemapController : Controller
{
    private readonly IConfiguration _configuration;
    private readonly string _baseUrl;

    public SitemapController(IConfiguration configuration)
    {
        _configuration = configuration;
        _baseUrl = _configuration["SiteSettings:BaseUrl"] ?? "https://stylobot.net";
    }

    [HttpGet("sitemap.xml")]
    [ResponseCache(Duration = 3600)] // Cache for 1 hour
    public IActionResult Sitemap()
    {
        var sitemap = GenerateSitemap();
        return Content(sitemap, "application/xml", Encoding.UTF8);
    }

    [HttpGet("robots.txt")]
    [ResponseCache(Duration = 86400)] // Cache for 24 hours
    public IActionResult RobotsTxt()
    {
        var robots = GenerateRobotsTxt();
        return Content(robots, "text/plain", Encoding.UTF8);
    }

    private string GenerateSitemap()
    {
        var urls = new List<SitemapUrl>
        {
            new() { Loc = _baseUrl, Priority = 1.0, ChangeFreq = "weekly" },
            new() { Loc = $"{_baseUrl}/Home/Features", Priority = 0.9, ChangeFreq = "monthly" },
            new() { Loc = $"{_baseUrl}/Home/Detectors", Priority = 0.9, ChangeFreq = "monthly" },
            new() { Loc = $"{_baseUrl}/Home/Enterprise", Priority = 0.8, ChangeFreq = "monthly" },
            new() { Loc = $"{_baseUrl}/Home/LiveDemo", Priority = 0.9, ChangeFreq = "weekly" },
            new() { Loc = $"{_baseUrl}/Home/Privacy", Priority = 0.5, ChangeFreq = "yearly" },
            new() { Loc = $"{_baseUrl}/Home/Contact", Priority = 0.6, ChangeFreq = "yearly" }
        };

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

        foreach (var url in urls)
        {
            sb.AppendLine("  <url>");
            sb.AppendLine($"    <loc>{url.Loc}</loc>");
            sb.AppendLine($"    <lastmod>{DateTime.UtcNow:yyyy-MM-dd}</lastmod>");
            sb.AppendLine($"    <changefreq>{url.ChangeFreq}</changefreq>");
            sb.AppendLine($"    <priority>{url.Priority:F1}</priority>");
            sb.AppendLine("  </url>");
        }

        sb.AppendLine("</urlset>");
        return sb.ToString();
    }

    private string GenerateRobotsTxt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("User-agent: *");
        sb.AppendLine("Allow: /");
        sb.AppendLine();
        sb.AppendLine("# Sitemaps");
        sb.AppendLine($"Sitemap: {_baseUrl}/sitemap.xml");
        sb.AppendLine();
        sb.AppendLine("# Crawl-delay (optional, uncomment if needed)");
        sb.AppendLine("# Crawl-delay: 10");

        return sb.ToString();
    }

    private class SitemapUrl
    {
        public string Loc { get; set; } = string.Empty;
        public double Priority { get; set; }
        public string ChangeFreq { get; set; } = string.Empty;
    }
}
