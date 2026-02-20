using Markdig;
using Stylobot.Website.Models;

namespace Stylobot.Website.Services;

public interface IMarkdownDocsService
{
    IReadOnlyList<DocsNavItem> GetNavigation();
    bool TryGetPage(string slug, out DocsPageViewModel? page);
}

public sealed class MarkdownDocsService : IMarkdownDocsService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseGenericAttributes()
        .Build();

    private readonly string _docsPath;

    public MarkdownDocsService(IWebHostEnvironment environment)
    {
        _docsPath = Path.Combine(environment.ContentRootPath, "Docs");
    }

    public IReadOnlyList<DocsNavItem> GetNavigation()
    {
        if (!Directory.Exists(_docsPath))
        {
            return [];
        }

        return Directory
            .GetFiles(_docsPath, "*.md", SearchOption.TopDirectoryOnly)
            .Select(ToNavItem)
            .OrderBy(item => NavOrder(item.Slug))
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool TryGetPage(string slug, out DocsPageViewModel? page)
    {
        page = null;

        var safeSlug = NormalizeSlug(slug);
        var filePath = Path.Combine(_docsPath, safeSlug + ".md");

        if (!File.Exists(filePath))
        {
            return false;
        }

        var markdown = File.ReadAllText(filePath);
        var html = Markdown.ToHtml(markdown, Pipeline);
        var title = ExtractTitle(markdown, safeSlug);

        page = new DocsPageViewModel(
            safeSlug,
            title,
            html,
            File.GetLastWriteTimeUtc(filePath),
            GetNavigation());

        return true;
    }

    private static string NormalizeSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "start-here";
        }

        var normalized = slug.Trim().ToLowerInvariant();
        return string.Concat(normalized.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
    }

    private static DocsNavItem ToNavItem(string path)
    {
        var slug = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var firstHeading = File.ReadLines(path)
            .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));

        var title = firstHeading?[2..].Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = slug.Replace('-', ' ');
        }

        return new DocsNavItem(slug, title);
    }

    private static int NavOrder(string slug)
    {
        return slug switch
        {
            "start-here" => 0,
            "how-stylobot-works" => 1,
            "detectors-in-depth" => 2,
            "running-locally" => 3,
            "live-demo" => 4,
            "deploy-on-server" => 5,
            "connected-signature-exchange-spec" => 6,
            "frequently-asked-questions" => 7,
            "glossary" => 8,
            "github-docs-map" => 9,
            _ => 100
        };
    }

    private static string ExtractTitle(string markdown, string fallbackSlug)
    {
        var firstHeading = markdown
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(firstHeading))
        {
            return firstHeading[2..].Trim();
        }

        return fallbackSlug.Replace('-', ' ');
    }
}
