#!/usr/bin/env dotnet-script
#r "nuget: OllamaSharp, 5.4.12"

using OllamaSharp;
using System.Text;

var ollama = new OllamaApiClient("http://localhost:11434")
{
    SelectedModel = "ministral-3:8b"
};

Console.WriteLine("Testing Ollama LLM User-Agent Generation\n");
Console.WriteLine("==========================================\n");

// Test 1: Generate a bot user-agent
Console.WriteLine("Test 1: Generating BOT user-agent...");
var botPrompt = @"Generate a realistic HTTP User-Agent string for a web bot/crawler/scraper.

Examples:
- Googlebot/2.1 (+http://www.google.com/bot.html)
- python-requests/2.31.0
- Scrapy/2.11.0 (+https://scrapy.org)
- curl/8.4.0

Return ONLY the User-Agent string. No prefix or explanation.";

var chat = new Chat(ollama);
var botResponse = new StringBuilder();
await foreach (var token in chat.SendAsync(botPrompt, CancellationToken.None))
{
    botResponse.Append(token);
}
var botUA = botResponse.ToString().Trim().Split('\n')[0].Trim();
Console.WriteLine($"Generated: {botUA}\n");

// Test 2: Generate a human user-agent
Console.WriteLine("Test 2: Generating HUMAN user-agent...");
var humanPrompt = @"Generate a realistic HTTP User-Agent string for a real web browser.

EXACT format for Chrome:
Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36

Return ONLY the User-Agent string.";

chat = new Chat(ollama);
var humanResponse = new StringBuilder();
await foreach (var token in chat.SendAsync(humanPrompt, CancellationToken.None))
{
    humanResponse.Append(token);
}
var humanUA = humanResponse.ToString().Trim().Split('\n')[0].Trim();
Console.WriteLine($"Generated: {humanUA}\n");

Console.WriteLine("==========================================");
Console.WriteLine("✅ Generation test complete!");
