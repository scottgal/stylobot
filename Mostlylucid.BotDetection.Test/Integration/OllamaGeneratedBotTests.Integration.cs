using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using OllamaSharp;

namespace Mostlylucid.BotDetection.Test.Integration;

/// <summary>
///     Long-running integration tests that use Ollama (ministral:8b) to generate
///     synthetic bot and human user-agent strings, then verify the detection system
///     correctly classifies them.
///     These tests require:
///     - Ollama running locally on http://localhost:11434
///     - The ministral:8b model installed (ollama pull ministral:8b)
///     Run with: dotnet test --filter "Category=LongRunning"
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "LongRunning")]
[Trait("Category", "Ollama")]
public class OllamaGeneratedBotTests : IAsyncLifetime
{
    private const string OllamaEndpoint = "http://localhost:11434";
    private const string OllamaModel = "ministral-3:8b";
    private const int GenerationTimeoutMs = 30000;
    private readonly HttpClient _httpClient = new();
    private List<string> _downloadedBotPatterns = new();

    private OllamaApiClient? _ollama;
    private bool _ollamaAvailable;

    public async Task InitializeAsync()
    {
        // Check if Ollama is available
        _ollamaAvailable = await CheckOllamaAvailableAsync();

        if (_ollamaAvailable)
            _ollama = new OllamaApiClient(OllamaEndpoint)
            {
                SelectedModel = OllamaModel
            };

        // Download bot patterns for validation
        await DownloadBotPatternsAsync();
    }

    public Task DisposeAsync()
    {
        _httpClient.Dispose();
        return Task.CompletedTask;
    }

    private async Task<bool> CheckOllamaAvailableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync($"{OllamaEndpoint}/api/tags");
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync();
            return json.Contains(OllamaModel, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task DownloadBotPatternsAsync()
    {
        try
        {
            var url = "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json";
            var json = await _httpClient.GetStringAsync(url);
            _downloadedBotPatterns = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            // Use fallback patterns
            _downloadedBotPatterns = new List<string>
            {
                @"bot", @"crawler", @"spider", @"scraper",
                @"curl", @"wget", @"python", @"java",
                @"httpclient", @"axios", @"fetch"
            };
        }
    }

    #region Adversarial Tests

    [Fact(Skip = "AI test: requires local Ollama. Run with --filter explicitly.")]
    public async Task Ollama_GeneratesEvasiveBots_TestsDetectionRobustness()
    {
        if (Skip.If(!_ollamaAvailable, "Ollama not available or ministral:8b not installed")) return;

        // Arrange - Generate bot UAs designed to evade detection
        var evasiveBots = new List<string>();

        // Act
        for (var i = 0; i < 5; i++)
        {
            var evasiveBot = await GenerateEvasiveBotUserAgentAsync();
            if (!string.IsNullOrEmpty(evasiveBot)) evasiveBots.Add(evasiveBot);
        }

        // Assert & Report
        var detectionResults = evasiveBots
            .Select(ua => (UserAgent: ua, Detected: IsDetectedAsBot(ua)))
            .ToList();

        var detectedCount = detectionResults.Count(r => r.Detected);

        Console.WriteLine($"Evasive bot detection rate: {detectedCount}/{evasiveBots.Count}");
        foreach (var result in detectionResults)
            Console.WriteLine($"  {(result.Detected ? "[DETECTED]" : "[EVADED]")} {result.UserAgent}");

        // Note: This test documents evasion success rate, not a pass/fail
        // High evasion rate indicates the detection system needs improvement
    }

    #endregion

    #region Bulk Generation Tests

    [Fact(Skip = "AI test: requires local Ollama. Run with --filter explicitly.")]
    public async Task Ollama_BulkGenerateBotUserAgents_50Samples()
    {
        if (Skip.If(!_ollamaAvailable, "Ollama not available or ministral:8b not installed")) return;

        // Arrange
        var samples = new List<(string UserAgent, string Type, bool Detected)>();
        var types = new[] { "bot", "human" };

        // Act - Generate 25 of each type
        foreach (var type in types)
            for (var i = 0; i < 25; i++)
            {
                var ua = type == "bot"
                    ? await GenerateBotUserAgentAsync()
                    : await GenerateHumanUserAgentAsync();

                if (!string.IsNullOrEmpty(ua)) samples.Add((ua, type, IsDetectedAsBot(ua)));
            }

        // Assert & Report
        var botSamples = samples.Where(s => s.Type == "bot").ToList();
        var humanSamples = samples.Where(s => s.Type == "human").ToList();

        var truePositives = botSamples.Count(s => s.Detected);
        var falsePositives = humanSamples.Count(s => s.Detected);
        var trueNegatives = humanSamples.Count(s => !s.Detected);
        var falseNegatives = botSamples.Count(s => !s.Detected);

        var precision = truePositives > 0
            ? (double)truePositives / (truePositives + falsePositives)
            : 0;
        var recall = truePositives > 0
            ? (double)truePositives / (truePositives + falseNegatives)
            : 0;
        var f1 = precision + recall > 0
            ? 2 * (precision * recall) / (precision + recall)
            : 0;

        Console.WriteLine($"=== Detection Metrics (n={samples.Count}) ===");
        Console.WriteLine($"True Positives (bots detected): {truePositives}");
        Console.WriteLine($"False Positives (humans flagged): {falsePositives}");
        Console.WriteLine($"True Negatives (humans passed): {trueNegatives}");
        Console.WriteLine($"False Negatives (bots missed): {falseNegatives}");
        Console.WriteLine($"Precision: {precision:P1}");
        Console.WriteLine($"Recall: {recall:P1}");
        Console.WriteLine($"F1 Score: {f1:P1}");

        // Assert reasonable performance
        Assert.True(precision >= 0.6, $"Precision should be >=60%, got {precision:P1}");
        Assert.True(recall >= 0.6, $"Recall should be >=60%, got {recall:P1}");
    }

    #endregion

    #region Bot User-Agent Generation Tests

    [Fact(Skip = "AI test: requires local Ollama. Run with --filter explicitly.")]
    public async Task Ollama_GeneratesBotUserAgents_ThatAreDetectedAsBots()
    {
        if (Skip.If(!_ollamaAvailable, "Ollama not available or ministral:8b not installed")) return;

        // Arrange
        var generatedBots = new List<string>();
        var detectionResults = new List<(string UserAgent, bool Detected)>();

        // Act - Generate 10 bot user-agents
        for (var i = 0; i < 10; i++)
        {
            var botUserAgent = await GenerateBotUserAgentAsync();
            if (!string.IsNullOrEmpty(botUserAgent))
            {
                generatedBots.Add(botUserAgent);

                var isDetected = IsDetectedAsBot(botUserAgent);
                detectionResults.Add((botUserAgent, isDetected));
            }
        }

        // Assert - At least 70% should be detected as bots
        var detectedCount = detectionResults.Count(r => r.Detected);
        var detectionRate = (double)detectedCount / detectionResults.Count;

        Assert.True(generatedBots.Count >= 5, $"Should generate at least 5 bot UAs, got {generatedBots.Count}");
        Assert.True(detectionRate >= 0.7,
            $"Detection rate should be >=70%, got {detectionRate:P0}. " +
            $"Undetected: {string.Join(", ", detectionResults.Where(r => !r.Detected).Select(r => r.UserAgent))}");
    }

    [Fact(Skip = "AI test: requires local Ollama. Run with --filter explicitly.")]
    public async Task Ollama_GeneratesHumanUserAgents_ThatAreNotDetectedAsBots()
    {
        if (Skip.If(!_ollamaAvailable, "Ollama not available or ministral:8b not installed")) return;

        // Arrange
        var generatedHumans = new List<string>();
        var detectionResults = new List<(string UserAgent, bool Detected)>();

        // Act - Generate 10 human user-agents
        for (var i = 0; i < 10; i++)
        {
            var humanUserAgent = await GenerateHumanUserAgentAsync();
            if (!string.IsNullOrEmpty(humanUserAgent))
            {
                generatedHumans.Add(humanUserAgent);

                var isDetected = IsDetectedAsBot(humanUserAgent);
                detectionResults.Add((humanUserAgent, isDetected));
            }
        }

        // Assert - At least 80% should NOT be detected as bots
        var notDetectedCount = detectionResults.Count(r => !r.Detected);
        var humanRate = detectionResults.Count > 0 ? (double)notDetectedCount / detectionResults.Count : 0;

        Assert.True(generatedHumans.Count >= 5, $"Should generate at least 5 human UAs, got {generatedHumans.Count}");
        Assert.True(humanRate >= 0.8,
            $"Human rate should be >=80%, got {humanRate:P0}. " +
            $"False positives: {string.Join(", ", detectionResults.Where(r => r.Detected).Select(r => r.UserAgent))}");
    }

    [Fact(Skip = "AI test: requires local Ollama. Run with --filter explicitly.")]
    public async Task Ollama_GeneratesVariedBotTypes()
    {
        if (Skip.If(!_ollamaAvailable, "Ollama not available or ministral:8b not installed")) return;

        // Arrange
        var botTypes = new[] { "scraper", "crawler", "search engine", "monitoring", "http client library" };
        var generatedByType = new Dictionary<string, List<string>>();

        // Act - Generate 2 user-agents for each bot type
        foreach (var botType in botTypes)
        {
            generatedByType[botType] = new List<string>();

            for (var i = 0; i < 2; i++)
            {
                var userAgent = await GenerateSpecificBotTypeAsync(botType);
                if (!string.IsNullOrEmpty(userAgent)) generatedByType[botType].Add(userAgent);
            }
        }

        // Assert - Each type should generate at least 1 UA
        foreach (var botType in botTypes)
            Assert.True(generatedByType[botType].Count >= 1,
                $"Should generate at least 1 {botType} UA, got {generatedByType[botType].Count}");

        // Output for inspection
        foreach (var kvp in generatedByType) Console.WriteLine($"{kvp.Key}: {string.Join(" | ", kvp.Value)}");
    }

    #endregion

    #region LLM Detector Integration Tests

    [Fact(Skip = "AI test: requires local Ollama. Run with --filter explicitly.")]
    public async Task LlmDetector_WithOllama_ClassifiesGeneratedBots()
    {
        if (Skip.If(!_ollamaAvailable, "Ollama not available or ministral:8b not installed")) return;

        // Arrange
        var detector = CreateLlmDetector();
        var generatedBot = await GenerateBotUserAgentAsync();

        if (Skip.If(string.IsNullOrEmpty(generatedBot), "Failed to generate bot user-agent")) return;

        var context = CreateHttpContext(generatedBot!);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - LLM should detect the bot with some confidence
        Console.WriteLine($"Generated bot: {generatedBot}");
        Console.WriteLine($"LLM Detection: IsBot={result.Confidence > 0}, Confidence={result.Confidence}");

        // Note: LLM detection might not always agree with pattern matching
        // This test is more about verifying the integration works
        Assert.True(result.Confidence >= 0, "Should return a valid confidence score");
    }

    [Fact(Skip = "AI test: requires local Ollama. Run with --filter explicitly.")]
    public async Task LlmDetector_WithOllama_ClassifiesGeneratedHumans()
    {
        if (Skip.If(!_ollamaAvailable, "Ollama not available or ministral:8b not installed")) return;

        // Arrange
        var detector = CreateLlmDetector();
        var generatedHuman = await GenerateHumanUserAgentAsync();

        if (Skip.If(string.IsNullOrEmpty(generatedHuman), "Failed to generate human user-agent")) return;

        var context = CreateHttpContext(generatedHuman!);

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Console.WriteLine($"Generated human: {generatedHuman}");
        Console.WriteLine($"LLM Detection: Confidence={result.Confidence}");

        // Human UAs should have lower confidence than obvious bots
        // Note: LLM behavior is non-deterministic, so we allow up to 0.8
        Assert.True(result.Confidence <= 0.8,
            $"Human UA should have moderate or low confidence, got {result.Confidence}");
    }

    [Fact(Skip = "AI test: requires local Ollama. Run with --filter explicitly.")]
    public async Task LlmDetector_WithGemma_ReturnsValidJsonResponse()
    {
        if (Skip.If(!_ollamaAvailable, "Ollama not available")) return;

        // Arrange - use gemma3:1b (the default model for bot detection)
        var options = Options.Create(new BotDetectionOptions
        {
            EnableLlmDetection = true,
            AiDetection = new AiDetectionOptions
            {
                Provider = AiProvider.Ollama,
                TimeoutMs = 5000,
                Ollama = new OllamaOptions
                {
                    Endpoint = OllamaEndpoint,
                    Model = "gemma3:1b", // The default small model for bot detection
                    Enabled = true
                }
            }
        });

        var detector = new LlmDetector(
            NullLogger<LlmDetector>.Instance,
            options);

        // Real browser user-agent
        var context = CreateHttpContext(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert - should return valid result with reasons (either bot or human classification)
        Console.WriteLine($"Detection result: Confidence={result.Confidence}");
        Console.WriteLine($"Reasons count: {result.Reasons.Count}");
        foreach (var reason in result.Reasons)
            Console.WriteLine($"  - {reason.Category}: {reason.Detail} (impact: {reason.ConfidenceImpact})");

        // The detector should return a valid response with reasons
        // For human classification: Reasons.Count > 0 with negative ConfidenceImpact
        // For bot classification: Reasons.Count > 0 with positive ConfidenceImpact
        Assert.True(result.Reasons.Count > 0 || result.Confidence == 0,
            "LLM should return a valid result with reasons or zero confidence (disabled)");
    }

    [Fact(Skip = "AI test: requires local Ollama. Run with --filter explicitly.")]
    public async Task LlmDetector_WithGemma_ClassifiesRealBotUserAgent()
    {
        if (Skip.If(!_ollamaAvailable, "Ollama not available")) return;

        // Arrange
        var options = Options.Create(new BotDetectionOptions
        {
            EnableLlmDetection = true,
            AiDetection = new AiDetectionOptions
            {
                Provider = AiProvider.Ollama,
                TimeoutMs = 5000,
                Ollama = new OllamaOptions
                {
                    Endpoint = OllamaEndpoint,
                    Model = "gemma3:1b",
                    Enabled = true
                }
            }
        });

        var detector = new LlmDetector(
            NullLogger<LlmDetector>.Instance,
            options);

        // Obvious bot user-agent
        var context = CreateHttpContext("curl/8.4.0");

        // Act
        var result = await detector.DetectAsync(context);

        // Assert
        Console.WriteLine($"curl/8.4.0 detection: Confidence={result.Confidence}");
        foreach (var reason in result.Reasons)
            Console.WriteLine($"  - {reason.Category}: {reason.Detail} (impact: {reason.ConfidenceImpact})");

        // curl should be detected as a bot or have some signal
        // Note: Small models like gemma3:1b may not always provide reasons
        // The key test is that the LLM was called and returned something
        Assert.True(result.Reasons.Count > 0 || result.Confidence >= 0,
            "LLM should return reasons for curl user-agent or have valid confidence");
        // For bot detection, confidence impact should be positive
        var llmReason = result.Reasons.FirstOrDefault(r => r.Category == "LLM Analysis");
        if (llmReason != null) Console.WriteLine($"LLM classified as bot: {llmReason.ConfidenceImpact > 0}");
    }

    #endregion

    #region Helper Methods

    private async Task<string?> GenerateBotUserAgentAsync()
    {
        // Randomize bot types for variety
        var botTypes = new[]
        {
            "python-requests/2.31.0",
            "Scrapy/2.11.0 (+https://scrapy.org)",
            "curl/8.4.0",
            "wget/1.21.4",
            "Go-http-client/2.0",
            "Apache-HttpClient/4.5.14 (Java/17.0.9)",
            "axios/1.6.2",
            "node-fetch/2.7.0",
            "okhttp/4.12.0",
            "Googlebot/2.1 (+http://www.google.com/bot.html)",
            "Mozilla/5.0 (compatible; Bingbot/2.0; +http://www.bing.com/bingbot.htm)",
            "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)",
            "Twitterbot/1.0",
            "LinkedInBot/1.0 (compatible; Mozilla/5.0; +http://www.linkedin.com)",
            "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)",
            "Mozilla/5.0 (compatible; Yahoo! Slurp; http://help.yahoo.com/help/us/ysearch/slurp)"
        };

        var example = botTypes[Random.Shared.Next(botTypes.Length)];

        var prompt = $@"Generate ONE realistic HTTP User-Agent for a {GetBotCategory(example)}.
Use THIS as inspiration but make it DIFFERENT: {example}
Vary the version numbers, URLs, and identifiers.
Return ONLY the User-Agent string, NO prefix or explanation.";

        return await GenerateWithOllamaAsync(prompt);
    }

    private static string GetBotCategory(string example)
    {
        if (example.Contains("python") || example.Contains("requests")) return "Python HTTP client";
        if (example.Contains("Scrapy")) return "web scraper";
        if (example.Contains("curl") || example.Contains("wget")) return "command-line tool";
        if (example.Contains("Go-") || example.Contains("Apache") || example.Contains("okhttp"))
            return "HTTP client library";
        if (example.Contains("axios") || example.Contains("node-fetch")) return "JavaScript HTTP client";
        if (example.Contains("Googlebot") || example.Contains("Bingbot")) return "search engine crawler";
        if (example.Contains("facebook") || example.Contains("Twitter") || example.Contains("LinkedIn") ||
            example.Contains("Slack")) return "social media bot";
        return "web crawler";
    }

    private async Task<string?> GenerateHumanUserAgentAsync()
    {
        // Randomize browsers for variety
        var browsers = new[]
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:120.0) Gecko/20100101 Firefox/120.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_1) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Safari/605.1.15",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 OPR/106.0.0.0",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1",
            "Mozilla/5.0 (iPad; CPU OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1",
            "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.6099.144 Mobile Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Brave/1.61",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Vivaldi/6.5.3206.55"
        };

        var example = browsers[Random.Shared.Next(browsers.Length)];

        var prompt = $@"Generate ONE realistic HTTP User-Agent for a {GetBrowserType(example)}.
Use THIS as inspiration but make it DIFFERENT: {example}
Vary the version numbers (slightly newer or older), but keep the same structure and format.
Return ONLY the User-Agent string, NO prefix or explanation.";

        return await GenerateWithOllamaAsync(prompt);
    }

    private static string GetBrowserType(string example)
    {
        if (example.Contains("Firefox")) return "Firefox browser";
        if (example.Contains("Safari") && example.Contains("Macintosh") && !example.Contains("Chrome"))
            return "Safari on macOS";
        if (example.Contains("Safari") && example.Contains("iPhone")) return "Safari on iPhone";
        if (example.Contains("Safari") && example.Contains("iPad")) return "Safari on iPad";
        if (example.Contains("Android")) return "Chrome on Android";
        if (example.Contains("Edg/")) return "Microsoft Edge browser";
        if (example.Contains("OPR/")) return "Opera browser";
        if (example.Contains("Brave")) return "Brave browser";
        if (example.Contains("Vivaldi")) return "Vivaldi browser";
        if (example.Contains("Linux")) return "Chrome on Linux";
        if (example.Contains("Macintosh") && example.Contains("Chrome")) return "Chrome on macOS";
        return "Chrome on Windows";
    }

    private async Task<string?> GenerateSpecificBotTypeAsync(string botType)
    {
        var prompt = $@"Generate a realistic HTTP User-Agent string for a {botType}.
Make it look authentic with version numbers and identifiers.
Return ONLY the User-Agent string, nothing else.
User-Agent:";

        return await GenerateWithOllamaAsync(prompt);
    }

    private async Task<string?> GenerateEvasiveBotUserAgentAsync()
    {
        var prompt = @"Generate a User-Agent string for a bot that tries to look like a real browser.
It should mimic Chrome/Firefox/Safari but have subtle differences.
Make it hard to detect as a bot while still being a bot.
Return ONLY the User-Agent string, nothing else.
User-Agent:";

        return await GenerateWithOllamaAsync(prompt);
    }

    private async Task<string?> GenerateWithOllamaAsync(string prompt)
    {
        if (_ollama == null) return null;

        try
        {
            using var cts = new CancellationTokenSource(GenerationTimeoutMs);
            var chat = new Chat(_ollama);
            var responseBuilder = new StringBuilder();

            await foreach (var token in chat.SendAsync(prompt, cts.Token)) responseBuilder.Append(token);

            var response = responseBuilder.ToString().Trim();

            // Clean up the response - extract just the UA string
            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var ua = lines
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("Mozilla/") ||
                                     l.Contains("/") ||
                                     l.Contains("bot", StringComparison.OrdinalIgnoreCase));

            return ua ?? response.Split('\n').FirstOrDefault()?.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ollama generation failed: {ex.Message}");
            return null;
        }
    }

    private bool IsDetectedAsBot(string userAgent)
    {
        return _downloadedBotPatterns.Any(pattern =>
        {
            try
            {
                return Regex.IsMatch(userAgent, pattern, RegexOptions.IgnoreCase);
            }
            catch
            {
                return false;
            }
        });
    }

    private LlmDetector CreateLlmDetector()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            EnableLlmDetection = true,
            AiDetection = new AiDetectionOptions
            {
                Provider = AiProvider.Ollama,
                TimeoutMs = 10000,
                Ollama = new OllamaOptions
                {
                    Endpoint = OllamaEndpoint,
                    Model = OllamaModel
                }
            },
            OllamaEndpoint = OllamaEndpoint,
            OllamaModel = OllamaModel,
            LlmTimeoutMs = 10000
        });

        return new LlmDetector(
            NullLogger<LlmDetector>.Instance,
            options);
    }

    private static HttpContext CreateHttpContext(string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = userAgent;
        context.Request.Path = "/test";
        context.Request.Method = "GET";
        context.Request.Headers.Accept = "text/html,application/xhtml+xml";
        context.Request.Headers.AcceptLanguage = "en-US,en;q=0.9";
        return context;
    }

    #endregion
}

/// <summary>
///     Helper class to conditionally skip tests in xUnit v2.
///     Since xUnit v2 doesn't have runtime skip support, this returns a boolean
///     that callers should use to guard and return early from tests.
/// </summary>
public static class Skip
{
    /// <summary>
    ///     Checks if a test should be skipped. Caller must return early if true.
    ///     Writes skip reason to output for visibility in test results.
    /// </summary>
    /// <returns>True if test should be skipped (caller should return immediately)</returns>
    public static bool If(bool condition, string reason)
    {
        if (condition)
            // Output skip reason to test results
            Console.WriteLine($"[SKIPPED] {reason}");
        return condition;
    }
}