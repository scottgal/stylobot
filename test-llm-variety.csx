#!/usr/bin/env dotnet-script
#r "nuget: OllamaSharp, 5.4.12"

using OllamaSharp;
using System.Text;
using System.Threading;

var ollama = new OllamaApiClient("http://localhost:11434")
{
    SelectedModel = "ministral-3:8b"
};

Console.WriteLine("=== Testing Bot UA Generation Variety ===\n");

var botTypes = new[]
{
    "python-requests/2.31.0",
    "Scrapy/2.11.0 (+https://scrapy.org)",
    "curl/8.4.0",
    "Googlebot/2.1 (+http://www.google.com/bot.html)"
};

foreach (var example in botTypes)
{
    var category = example.Contains("python") ? "Python HTTP client" :
                   example.Contains("Scrapy") ? "web scraper" :
                   example.Contains("curl") ? "command-line tool" :
                   "search engine crawler";

    var prompt = $@"Generate ONE realistic HTTP User-Agent for a {category}.
Use THIS as inspiration but make it DIFFERENT: {example}
Vary the version numbers, URLs, and identifiers.
Return ONLY the User-Agent string, NO prefix or explanation.";

    var chat = new Chat(ollama);
    var response = new StringBuilder();

    await foreach (var token in chat.SendAsync(prompt, CancellationToken.None))
    {
        response.Append(token);
    }

    var ua = response.ToString().Trim().Split('\n')[0];
    Console.WriteLine($"{category,-25} => {ua}");
}

Console.WriteLine("\n=== Testing Human UA Generation Variety ===\n");

var browsers = new[]
{
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
    "Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1"
};

foreach (var example in browsers)
{
    var browserType = example.Contains("Firefox") ? "Firefox browser" :
                      example.Contains("iPhone") ? "Safari on iPhone" :
                      example.Contains("Macintosh") && !example.Contains("Chrome") ? "Safari on macOS" :
                      "Chrome on Windows";

    var prompt = $@"Generate ONE realistic HTTP User-Agent for a {browserType}.
Use THIS as inspiration but make it DIFFERENT: {example}
Vary the version numbers (slightly newer or older), but keep the same structure and format.
Return ONLY the User-Agent string, NO prefix or explanation.";

    var chat = new Chat(ollama);
    var response = new StringBuilder();

    await foreach (var token in chat.SendAsync(prompt, CancellationToken.None))
    {
        response.Append(token);
    }

    var ua = response.ToString().Trim().Split('\n')[0];
    Console.WriteLine($"{browserType,-25} => {ua}");
}

Console.WriteLine("\n✅ Variety test complete!");
