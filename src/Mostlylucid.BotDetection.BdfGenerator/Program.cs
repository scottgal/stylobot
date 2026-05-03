using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Behavioral;
using OllamaSharp;
using OllamaSharp.Models.Chat;

namespace Mostlylucid.BotDetection.BdfGenerator;

/// <summary>
///     CLI app that generates BDF scenarios for all bot categories using Ollama.
///
///     This tool:
///     1. Loads bot patterns from the bot detection database
///     2. Groups bots by category (Scraper, SearchEngine, Monitor, etc.)
///     3. Uses Ollama (mistral-3:8b) to generate realistic BDF scenarios for each category
///     4. Includes test IP markers for Project Honeypot simulation
///     5. Outputs JSON scenario files for regression testing
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<BdfScenarioGenerator>();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var host = builder.Build();
        var generator = host.Services.GetRequiredService<BdfScenarioGenerator>();

        try
        {
            await generator.GenerateScenariosAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }
}

public class BdfScenarioGenerator
{
    private readonly ILogger<BdfScenarioGenerator> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _databasePath;
    private readonly string _outputPath;
    private readonly OllamaApiClient _ollama;

    public BdfScenarioGenerator(
        ILogger<BdfScenarioGenerator> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _databasePath = FindBotDetectionDatabase();
        _outputPath = Path.Combine(AppContext.BaseDirectory, "generated-scenarios");
        _ollama = new OllamaApiClient("http://localhost:11434");
    }

    private static string FindBotDetectionDatabase()
    {
        // Search for botdetection.db in common locations
        var searchPaths = new[]
        {
            // Current directory
            Path.Combine(AppContext.BaseDirectory, "botdetection.db"),
            // Demo bin directory (net9.0)
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Mostlylucid.BotDetection.Demo", "bin", "Debug", "net9.0", "botdetection.db"),
            // Demo bin directory (net10.0)
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Mostlylucid.BotDetection.Demo", "bin", "Debug", "net10.0", "botdetection.db"),
            // Parent directory
            Path.Combine(AppContext.BaseDirectory, "..", "botdetection.db"),
            // Root source directory
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "botdetection.db")
        };

        foreach (var path in searchPaths)
        {
            var normalizedPath = Path.GetFullPath(path);
            if (File.Exists(normalizedPath))
            {
                return normalizedPath;
            }
        }

        // Default fallback
        return Path.Combine(AppContext.BaseDirectory, "botdetection.db");
    }

    public async Task GenerateScenariosAsync()
    {
        _logger.LogInformation("BDF Scenario Generator starting...");
        _logger.LogInformation("Database path: {Path}", _databasePath);

        // Create output directory
        Directory.CreateDirectory(_outputPath);

        // Check if database exists
        if (!File.Exists(_databasePath))
        {
            _logger.LogError("Bot detection database not found at {Path}", _databasePath);
            _logger.LogInformation("Searched in common locations but couldn't find botdetection.db");
            _logger.LogInformation("Please run the bot detection demo first to download bot lists, or copy the database to this directory.");
            return;
        }

        // Load bot categories from database
        var categories = await LoadBotCategoriesAsync();
        _logger.LogInformation("Found {Count} bot categories", categories.Count);

        foreach (var (category, userAgents) in categories)
        {
            _logger.LogInformation("Generating scenarios for category: {Category} ({Count} samples)",
                category, userAgents.Count);

            await GenerateScenariosForCategoryAsync(category, userAgents);
        }

        _logger.LogInformation("✓ Scenario generation complete! Output: {Path}", _outputPath);
        _logger.LogInformation("Generated scenarios can be used for bot detection regression testing.");
    }

    private async Task<Dictionary<string, List<string>>> LoadBotCategoriesAsync()
    {
        var categories = new Dictionary<string, List<string>>();

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT category, name, pattern
            FROM bot_patterns
            WHERE category IS NOT NULL AND category != ''
            ORDER BY category, name
            LIMIT 1000";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var category = reader.GetString(0);
            var name = reader.GetString(1);
            var pattern = reader.GetString(2);

            if (!categories.ContainsKey(category))
                categories[category] = new List<string>();

            // Use bot name as a sample user agent
            var sampleUA = ConvertPatternToSampleUA(name, pattern);
            if (!string.IsNullOrWhiteSpace(sampleUA))
                categories[category].Add(sampleUA);
        }

        return categories;
    }

    private string ConvertPatternToSampleUA(string name, string pattern)
    {
        // Simple heuristic: if pattern looks like a plain name, use it
        // Otherwise, use the bot name
        if (!pattern.Contains("[") && !pattern.Contains("(") && !pattern.Contains("|"))
            return pattern;

        // Common bot user agents
        return name switch
        {
            var n when n.Contains("Googlebot", StringComparison.OrdinalIgnoreCase) =>
                "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
            var n when n.Contains("Bingbot", StringComparison.OrdinalIgnoreCase) =>
                "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",
            var n when n.Contains("Slackbot", StringComparison.OrdinalIgnoreCase) =>
                "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)",
            var n when n.Contains("crawler", StringComparison.OrdinalIgnoreCase) =>
                $"{name}/1.0 (Web Crawler; +http://example.com/bot)",
            var n when n.Contains("scraper", StringComparison.OrdinalIgnoreCase) =>
                $"{name}/1.0 (Scraper Bot)",
            var n when n.Contains("spider", StringComparison.OrdinalIgnoreCase) =>
                $"{name}/1.0 (Web Spider; +http://example.com/spider)",
            _ => $"{name}/1.0"
        };
    }

    private async Task GenerateScenariosForCategoryAsync(string category, List<string> userAgents)
    {
        // Take up to 3 sample user agents
        var samples = userAgents.Take(3).ToList();

        // Define bot profiles based on category
        var profile = DetermineBotProfile(category);

        // Generate scenarios using Ollama
        foreach (var (behaviorType, testType) in profile.BehaviorTypes)
        {
            try
            {
                var scenario = await GenerateScenarioWithOllamaAsync(
                    category,
                    behaviorType,
                    samples,
                    testType);

                if (scenario != null)
                {
                    await SaveScenarioAsync(scenario, category, behaviorType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate {Type} scenario for {Category}",
                    behaviorType, category);
            }
        }
    }

    private BotProfile DetermineBotProfile(string category)
    {
        return category.ToLowerInvariant() switch
        {
            "scraper" => new BotProfile
            {
                BehaviorTypes = new[]
                {
                    ("aggressive-burst", "harvester"),
                    ("sequential-scraper", "harvester"),
                    ("polite-crawler", null)
                }
            },
            "search engine" or "searchengine" => new BotProfile
            {
                BehaviorTypes = new (string, string?)[]
                {
                    ("polite-crawler", null),
                    ("respectful-indexer", null)
                }
            },
            "monitor" or "monitoring" => new BotProfile
            {
                BehaviorTypes = new (string, string?)[]
                {
                    ("periodic-monitor", null),
                    ("health-check", null)
                }
            },
            "feed" or "feed reader" => new BotProfile
            {
                BehaviorTypes = new (string, string?)[]
                {
                    ("rss-poller", null),
                    ("feed-aggregator", null)
                }
            },
            "social" or "social media" => new BotProfile
            {
                BehaviorTypes = new (string, string?)[]
                {
                    ("link-preview", null),
                    ("unfurler", null)
                }
            },
            "security" or "scanner" => new BotProfile
            {
                BehaviorTypes = new[]
                {
                    ("vulnerability-scanner", "suspicious"),
                    ("path-discovery", "suspicious")
                }
            },
            _ => new BotProfile
            {
                BehaviorTypes = new (string, string?)[]
                {
                    ("generic-bot", null)
                }
            }
        };
    }

    private async Task<BdfScenario?> GenerateScenarioWithOllamaAsync(
        string category,
        string behaviorType,
        List<string> sampleUserAgents,
        string? honeypotTestType)
    {
        _logger.LogInformation("  → Generating {Type} scenario...", behaviorType);

        var prompt = BuildPrompt(category, behaviorType, sampleUserAgents, honeypotTestType);

        try
        {
            var chat = new Chat(_ollama, "mistral-3:8b");
            var responseBuilder = new System.Text.StringBuilder();

            await foreach (var chunk in chat.SendAsync(prompt, CancellationToken.None))
            {
                responseBuilder.Append(chunk);
            }

            var response = responseBuilder.ToString();

            // Parse JSON from response (may be wrapped in markdown code blocks)
            var json = ExtractJsonFromResponse(response);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("No JSON found in Ollama response for {Type}", behaviorType);
                return null;
            }

            var scenario = JsonSerializer.Deserialize<BdfScenario>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });

            return scenario;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama generation failed for {Type}", behaviorType);
            return null;
        }
    }

    private string BuildPrompt(
        string category,
        string behaviorType,
        List<string> sampleUserAgents,
        string? honeypotTestType)
    {
        var userAgentList = string.Join("\n", sampleUserAgents.Select(ua => $"- {ua}"));

        var honeypotMarker = honeypotTestType != null
            ? $"<test-honeypot:{honeypotTestType}>"
            : "";

        return $$$"""
You are a bot behavior expert. Generate a realistic BDF (Behavioral Description Format) scenario JSON for testing bot detection systems.

Bot Category: {{{category}}}
Behavior Type: {{{behaviorType}}}
Sample User-Agents:
{{{userAgentList}}}

{{{(honeypotTestType != null ? $"IMPORTANT: This bot should trigger Project Honeypot detection. Add '{honeypotMarker}' to the user agent to simulate honeypot detection." : "")}}}

Generate a complete BDF JSON scenario that simulates a {{{behaviorType}}} bot in the '{{{category}}}' category.

Timing guidance for {{{behaviorType}}}:
{{{GetTimingGuidance(behaviorType)}}}

Navigation guidance for {{{behaviorType}}}:
{{{GetNavigationGuidance(behaviorType)}}}

Return ONLY valid JSON matching this structure (no explanations, no markdown):
{
  "version": "1.0",
  "id": "scenario-{{{category.ToLowerInvariant().Replace(" ", "-")}}}-{{{behaviorType}}}",
  "description": "Realistic {{{behaviorType}}} behavior for {{{category}}}",
  "metadata": {
    "author": "BDF-Generator",
    "createdUtc": "{{{DateTime.UtcNow:O}}}",
    "tags": ["{{{category.ToLowerInvariant()}}}", "{{{behaviorType}}}"]
  },
  "client": {
    "signatureId": "test-{{{category.ToLowerInvariant()}}}-{{{behaviorType}}}",
    "userAgent": "{{{(honeypotTestType != null ? $"{{sample-ua}} {honeypotMarker}" : "{sample-ua}")}}}",
    "headers": {
      "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
      "Accept-Language": "en-US,en;q=0.5"
    }
  },
  "expectation": {
    "expectedClassification": "{{{(honeypotTestType != null ? "Bot" : GetExpectedClassification(category, behaviorType))}}}",
    "minBotProbability": {{{GetExpectedProbability(behaviorType)}}},
    "minRiskBand": "{{{GetExpectedRiskBand(behaviorType)}}}"
  },
  "phases": [
    {{... generate realistic phase configuration based on guidance above ...}}
  ]
}
""";
    }

    private string GetTimingGuidance(string behaviorType)
    {
        return behaviorType switch
        {
            "aggressive-burst" => "Use timing mode 'burst' with BurstSize: 20, BurstIntervalSeconds: 5, BaseRateRps: 4.0",
            "sequential-scraper" => "Use timing mode 'fixed' with BaseRateRps: 2.0 (every 500ms)",
            "polite-crawler" => "Use timing mode 'jittered' with BaseRateRps: 0.5, JitterStdDevSeconds: 0.3",
            "respectful-indexer" => "Use timing mode 'jittered' with BaseRateRps: 0.2, JitterStdDevSeconds: 0.5",
            "periodic-monitor" => "Use timing mode 'fixed' with BaseRateRps: 0.0167 (every 60 seconds)",
            "health-check" => "Use timing mode 'fixed' with BaseRateRps: 0.0033 (every 300 seconds)",
            "rss-poller" => "Use timing mode 'fixed' with BaseRateRps: 0.0167 (every 60 seconds)",
            "vulnerability-scanner" => "Use timing mode 'burst' with BurstSize: 50, BurstIntervalSeconds: 2, BaseRateRps: 10.0",
            "path-discovery" => "Use timing mode 'fixed' with BaseRateRps: 5.0",
            _ => "Use timing mode 'jittered' with BaseRateRps: 1.0, JitterStdDevSeconds: 0.2"
        };
    }

    private string GetNavigationGuidance(string behaviorType)
    {
        return behaviorType switch
        {
            "aggressive-burst" or "sequential-scraper" =>
                "Use navigation mode 'sequential' with OffGraphProbability: 0.6, Paths: sequentially enumerate /page/1, /page/2, etc.",
            "vulnerability-scanner" or "path-discovery" =>
                "Use navigation mode 'scanner' with OffGraphProbability: 0.9, Paths: ['/wp-login.php', '/.git/HEAD', '/admin', '/phpmyadmin/', '/.env']",
            "polite-crawler" or "respectful-indexer" =>
                "Use navigation mode 'ui_graph' with OffGraphProbability: 0.1, Paths: follow typical navigation paths like /, /about, /products",
            "periodic-monitor" or "health-check" =>
                "Use navigation mode 'sequential' with OffGraphProbability: 0.0, Paths: ['/health', '/status']",
            "rss-poller" or "feed-aggregator" =>
                "Use navigation mode 'sequential' with OffGraphProbability: 0.0, Paths: ['/feed.xml', '/rss', '/atom.xml']",
            _ =>
                "Use navigation mode 'random' with OffGraphProbability: 0.3, Paths: random selection from typical pages"
        };
    }

    private string GetExpectedClassification(string category, string behaviorType)
    {
        if (behaviorType.Contains("scanner") || behaviorType.Contains("aggressive"))
            return "Bot";
        if (behaviorType.Contains("polite") || behaviorType.Contains("respectful"))
            return "Mixed";
        return "Bot";
    }

    private double GetExpectedProbability(string behaviorType)
    {
        return behaviorType switch
        {
            "aggressive-burst" => 0.95,
            "vulnerability-scanner" => 0.95,
            "path-discovery" => 0.90,
            "sequential-scraper" => 0.85,
            "periodic-monitor" => 0.70,
            "polite-crawler" => 0.50,
            "respectful-indexer" => 0.40,
            _ => 0.70
        };
    }

    private string GetExpectedRiskBand(string behaviorType)
    {
        return behaviorType switch
        {
            "vulnerability-scanner" or "path-discovery" or "aggressive-burst" => "High",
            "sequential-scraper" => "Medium",
            _ => "Low"
        };
    }

    private string ExtractJsonFromResponse(string response)
    {
        // Remove markdown code blocks if present
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```json"))
        {
            trimmed = trimmed["```json".Length..];
        }
        else if (trimmed.StartsWith("```"))
        {
            trimmed = trimmed["```".Length..];
        }

        if (trimmed.EndsWith("```"))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed.Trim();
    }

    private async Task SaveScenarioAsync(BdfScenario scenario, string category, string behaviorType)
    {
        var filename = $"{category.Replace(" ", "-").ToLowerInvariant()}-{behaviorType}.json";
        var filepath = Path.Combine(_outputPath, filename);

        var json = JsonSerializer.Serialize(scenario, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        await File.WriteAllTextAsync(filepath, json);

        _logger.LogInformation("    ✓ Saved: {Filename}", filename);
    }

    private record BotProfile
    {
        public required (string BehaviorType, string? HoneypotTestType)[] BehaviorTypes { get; init; }
    }
}
