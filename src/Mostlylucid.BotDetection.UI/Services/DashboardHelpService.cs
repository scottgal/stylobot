using System.Collections.Concurrent;
using System.Reflection;
using Markdig;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Loads markdown help files from embedded resources, renders to HTML via Markdig,
///     and caches the result. Supports additional help files from commercial assemblies.
/// </summary>
public sealed class DashboardHelpService
{
    private readonly ConcurrentDictionary<string, HelpEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly MarkdownPipeline _pipeline;
    private readonly ILogger<DashboardHelpService> _logger;

    public DashboardHelpService(ILogger<DashboardHelpService> logger)
    {
        _logger = logger;
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseGenericAttributes()
            .Build();

        // Load FOSS help files from this assembly
        LoadFromAssembly(typeof(DashboardHelpService).Assembly, commercial: false);

        // Auto-discover commercial help files from all loaded assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == typeof(DashboardHelpService).Assembly) continue;
            try
            {
                var helpResources = asm.GetManifestResourceNames()
                    .Where(n => n.Contains(".Help.") && n.EndsWith(".md", StringComparison.Ordinal));
                if (helpResources.Any())
                    LoadFromAssembly(asm, commercial: true);
            }
            catch { /* skip assemblies that can't enumerate resources */ }
        }
    }

    /// <summary>Register additional help files from a commercial assembly.</summary>
    public void LoadFromAssembly(Assembly assembly, bool commercial)
    {
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".md", StringComparison.Ordinal))
                     .Where(n => n.Contains(".Help.") || n.Contains(".Help\\"))
                     .OrderBy(n => n))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                using var reader = new StreamReader(stream);
                var markdown = reader.ReadToEnd();

                var entry = ParseHelpEntry(markdown, commercial);
                if (entry is not null)
                {
                    _entries[entry.Id] = entry;
                    _logger.LogDebug("Loaded help: {Id} ({Title})", entry.Id, entry.Title);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load help from {Resource}", resourceName);
            }
        }

        _logger.LogInformation("Help system loaded {Count} entries ({Commercial})",
            _entries.Count, commercial ? "commercial" : "FOSS");
    }

    /// <summary>Get rendered HTML for a help section. Returns null if not found or not authorized.</summary>
    public HelpEntry? GetHelp(string sectionId, bool isCommercial)
    {
        if (!_entries.TryGetValue(sectionId, out var entry)) return null;
        if (entry.Commercial && !isCommercial) return null;
        return entry;
    }

    /// <summary>Get all available help entries (filtered by commercial mode).</summary>
    public IReadOnlyList<HelpEntry> GetAll(bool isCommercial) =>
        _entries.Values
            .Where(e => !e.Commercial || isCommercial)
            .OrderBy(e => e.Section)
            .ThenBy(e => e.Title)
            .ToList();

    private HelpEntry? ParseHelpEntry(string markdown, bool commercial)
    {
        // Parse YAML front-matter (simple: between --- lines)
        string id = "", title = "", section = "";
        var isCommercial = commercial;
        var bodyStart = 0;

        if (markdown.StartsWith("---"))
        {
            var endIdx = markdown.IndexOf("---", 3, StringComparison.Ordinal);
            if (endIdx > 0)
            {
                var frontMatter = markdown[3..endIdx].Trim();
                bodyStart = endIdx + 3;

                foreach (var line in frontMatter.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var colonIdx = line.IndexOf(':');
                    if (colonIdx < 0) continue;
                    var key = line[..colonIdx].Trim();
                    var value = line[(colonIdx + 1)..].Trim().Trim('"');

                    switch (key)
                    {
                        case "id": id = value; break;
                        case "title": title = value; break;
                        case "section": section = value; break;
                        case "commercial": isCommercial = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(id)) return null;

        var body = markdown[bodyStart..].Trim();
        var html = Markdig.Markdown.ToHtml(body, _pipeline);

        return new HelpEntry
        {
            Id = id,
            Title = string.IsNullOrEmpty(title) ? id : title,
            Section = section,
            Commercial = isCommercial,
            Html = html
        };
    }
}

public sealed record HelpEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Section { get; init; } = "";
    public bool Commercial { get; init; }
    public required string Html { get; init; }
}
