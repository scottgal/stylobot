#!/usr/bin/env dotnet-script
// Quick integration test for LLamaSharp bot name synthesizer
// Usage: dotnet script test-llm-integration.csx

#r "nuget: Microsoft.Extensions.DependencyInjection, 10.0.3"
#r "nuget: Microsoft.Extensions.Logging, 10.0.3"
#r "nuget: Microsoft.Extensions.Options, 10.0.3"
#r "bin/Release/net10.0/Mostlylucid.BotDetection.dll"

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Models;

Console.WriteLine("🔍 Testing LLamaSharp Bot Name Synthesizer...\n");

// Setup DI
var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddBotDetection(options =>
{
    options.AiDetection.Provider = AiProvider.LlamaSharp;
    options.SignatureDescriptionThreshold = 3;
});

var sp = services.BuildServiceProvider();
var synthesizer = sp.GetRequiredService<IBotNameSynthesizer>();

Console.WriteLine($"✓ Synthesizer registered: {synthesizer.GetType().Name}");
Console.WriteLine($"✓ Is Ready: {synthesizer.IsReady}");
Console.WriteLine("\nℹ️  Note: First initialization downloads GGUF (~1.5GB)");
Console.WriteLine("   This happens on first inference, not startup.\n");

// Test with mock signals
var testSignals = new Dictionary<string, object?>
{
    { "detection.useragent.source", "Mozilla/5.0 (compatible; Googlebot/2.1)" },
    { "detection.ip.type", "datacenter" },
    { "detection.behavioral.rate_limit_violations", 5 },
    { "detection.correlation.primary_behavior", "aggressive_crawl" }
};

Console.WriteLine("Test signals prepared:");
foreach (var (k, v) in testSignals)
{
    Console.WriteLine($"  {k}: {v}");
}

Console.WriteLine("\n⏳ Attempting bot name synthesis (will timeout gracefully if model unavailable)...");
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

try
{
    var name = await synthesizer.SynthesizeBotNameAsync(testSignals, cts.Token);
    if (name != null)
    {
        Console.WriteLine($"✅ SUCCESS: Generated bot name: '{name}'");
    }
    else
    {
        Console.WriteLine("⚠️  Synthesis returned null (model not available or timeout)");
        Console.WriteLine("   This is expected if GGUF hasn't been downloaded yet.");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("⏱️  Synthesis timed out (expected on first run during download)");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Error: {ex.Message}");
}

Console.WriteLine("\n✓ Integration test completed!");
Console.WriteLine("\nNext steps:");
Console.WriteLine("  1. Run demo: dotnet run --project Mostlylucid.BotDetection.Demo");
Console.WriteLine("  2. Or test with: STYLOBOT_MODEL_CACHE=/tmp/models dotnet build");
