# BDF Scenario Generator

AI-powered CLI tool that generates realistic BDF (Behavioral Description Format) scenarios for bot detection regression testing.

## Overview

This tool uses Ollama AI (mistral-3:8b) to generate comprehensive BDF test scenarios covering all bot categories found in your bot detection database. It creates realistic behavioral profiles including:

- **Timing patterns** (fixed, jittered, burst)
- **Navigation patterns** (sequential, scanner, ui_graph, random)
- **Error interaction** patterns
- **Project Honeypot test markers** for simulation
- **Expected detection outcomes** for validation

## Prerequisites

1. **Ollama** must be running locally
   ```bash
   # Install Ollama from https://ollama.ai
   # Pull the mistral-3:8b model
   ollama pull mistral-3:8b

   # Start Ollama server (usually runs automatically)
   ollama serve
   ```

2. **Bot Detection Database** must exist
   - Run the Mostlylucid.BotDetection.Demo at least once to download bot lists
   - This creates `botdetection.db` with all bot categories

## Usage

### Run the Generator

The generator will automatically search for `botdetection.db` in common locations (current directory, demo bin directories, etc.).

```bash
cd Mostlylucid.BotDetection.BdfGenerator
dotnet run
```

**If the database is not found**, copy it from the demo app:

```bash
# Windows PowerShell
cp ..\Mostlylucid.BotDetection.Demo\bin\Debug\net9.0\botdetection.db .

# Or from your current directory if demo is running
# The generator will automatically search the demo's bin directory
```

The tool will:

1. Load bot categories from `botdetection.db`
2. For each category (Scraper, SearchEngine, Monitor, etc.):
   - Generate 2-3 behavioral variants per category
   - Use Ollama to create realistic BDF JSON scenarios
   - Include Project Honeypot test markers where appropriate
3. Save scenarios to `generated-scenarios/` directory

### Output

Scenarios are saved as JSON files:

```
generated-scenarios/
├── scraper-aggressive-burst.json
├── scraper-sequential-scraper.json
├── scraper-polite-crawler.json
├── search-engine-polite-crawler.json
├── search-engine-respectful-indexer.json
├── monitor-periodic-monitor.json
├── monitor-health-check.json
├── security-vulnerability-scanner.json
├── security-path-discovery.json
└── ... (more scenarios)
```

## Generated Scenario Examples

### Aggressive Scraper (with Honeypot Marker)

```json
{
  "id": "scenario-scraper-aggressive-burst",
  "description": "Realistic aggressive-burst behavior for Scraper",
  "client": {
    "signatureId": "test-scraper-aggressive-burst",
    "userAgent": "ScraperBot/1.0 <test-honeypot:harvester>",
    "headers": {
      "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
    }
  },
  "expectation": {
    "expectedClassification": "Bot",
    "minBotProbability": 0.95,
    "minRiskBand": "High"
  },
  "phases": [
    {
      "name": "main-attack",
      "requestCount": 100,
      "timing": {
        "mode": "burst",
        "baseRateRps": 4.0,
        "burst": {
          "burstSize": 20,
          "burstIntervalSeconds": 5
        }
      },
      "navigation": {
        "mode": "sequential",
        "offGraphProbability": 0.6,
        "paths": [
          {
            "template": "/page/{id}",
            "weight": 1.0,
            "idRange": { "min": 1, "max": 1000 }
          }
        ]
      }
    }
  ]
}
```

### Polite Search Engine

```json
{
  "id": "scenario-search-engine-polite-crawler",
  "client": {
    "userAgent": "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"
  },
  "expectation": {
    "expectedClassification": "Mixed",
    "minBotProbability": 0.50,
    "minRiskBand": "Low"
  },
  "phases": [
    {
      "timing": {
        "mode": "jittered",
        "baseRateRps": 0.5,
        "jitterStdDevSeconds": 0.3
      },
      "navigation": {
        "mode": "ui_graph",
        "offGraphProbability": 0.1,
        "paths": [
          { "template": "/", "weight": 1.0 },
          { "template": "/about", "weight": 1.0 },
          { "template": "/products", "weight": 2.0 }
        ]
      }
    }
  ]
}
```

## Bot Categories & Profiles

The generator creates behavior variants for each category:

| Category | Behavior Variants | Honeypot Test |
|----------|------------------|---------------|
| **Scraper** | aggressive-burst, sequential-scraper, polite-crawler | harvester |
| **Search Engine** | polite-crawler, respectful-indexer | none |
| **Monitor** | periodic-monitor, health-check | none |
| **Feed** | rss-poller, feed-aggregator | none |
| **Social** | link-preview, unfurler | none |
| **Security** | vulnerability-scanner, path-discovery | suspicious |

## Project Honeypot Test Markers

Scenarios for malicious bot types automatically include test markers in the User-Agent:

- `<test-honeypot:harvester>` - Email/content harvesters (threat score: 75)
- `<test-honeypot:spammer>` - Comment spammers (threat score: 100)
- `<test-honeypot:suspicious>` - Suspicious activity (threat score: 35)

These markers trigger the ProjectHoneypotContributor's test mode, simulating realistic honeypot detection without actual DNS lookups.

## Using Generated Scenarios

### 1. Manual Testing

```bash
# Copy database to BDF generator directory
cp ../Mostlylucid.BotDetection.Demo/bin/Debug/net9.0/botdetection.db .

# Run generator
dotnet run

# Use BDF runner to test scenarios
cd ../Mostlylucid.BotDetection.Tests
dotnet test --filter "Category=BdfRegression"
```

### 2. Regression Testing

```csharp
[Theory]
[MemberData(nameof(GetGeneratedScenarios))]
public async Task BotDetection_GeneratedScenario_MatchesExpectation(string scenarioFile)
{
    // Load scenario
    var scenario = await _bdfRunner.LoadScenarioAsync(scenarioFile);

    // Execute against test environment
    var result = await _bdfRunner.RunScenarioAsync(scenario, TestBaseUrl);

    // Validate expectations
    Assert.True(result.ExpectationMet,
        $"Scenario {scenario.Id} failed expectations: {result.PhaseResults.First().Requests.First().StatusCode}");
}

public static IEnumerable<object[]> GetGeneratedScenarios()
{
    var scenariosPath = Path.Combine(AppContext.BaseDirectory, "generated-scenarios");
    return Directory.GetFiles(scenariosPath, "*.json")
        .Select(f => new object[] { f });
}
```

### 3. CI/CD Integration

```yaml
# .github/workflows/bot-detection-regression.yml
name: Bot Detection Regression Tests

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3

      - name: Setup .NET
        uses: actions/setup-dotnet@v3

      - name: Install Ollama
        run: curl https://ollama.ai/install.sh | sh

      - name: Pull mistral-3:8b
        run: ollama pull mistral-3:8b

      - name: Generate BDF scenarios
        run: |
          cd Mostlylucid.BotDetection.BdfGenerator
          dotnet run

      - name: Run regression tests
        run: |
          cd Mostlylucid.BotDetection.Tests
          dotnet test --filter "Category=BdfRegression"
```

## Troubleshooting

### "Bot detection database not found"

The generator automatically searches these locations:
- Current directory (`./botdetection.db`)
- Demo bin directories (`../Mostlylucid.BotDetection.Demo/bin/Debug/net9.0/botdetection.db`)
- Parent directories

**Solution 1:** Run the demo app first (if not already done):

```bash
cd ../Mostlylucid.BotDetection.Demo
dotnet run
# Visit http://localhost:5000 to trigger list download
```

**Solution 2:** Copy the database manually:

```bash
# From BdfGenerator directory
cp ../Mostlylucid.BotDetection.Demo/bin/Debug/net9.0/botdetection.db .
```

### "Ollama connection refused"

**Solution:** Ensure Ollama is running:

```bash
ollama serve
# In another terminal:
ollama list  # Should show mistral-3:8b
```

### "No JSON found in Ollama response"

**Problem:** Ollama may not have generated valid JSON.

**Solution:**
- Check Ollama logs for errors
- Try using a different model: `ollama pull mistral:7b`
- Adjust the prompt if needed (see `BuildPrompt()` method)

### Generated scenarios have incorrect timing

**Solution:** Edit the guidance in `GetTimingGuidance()` method to adjust RPS/burst parameters.

## Customization

### Add New Bot Category

Edit `DetermineBotProfile()` in `Program.cs`:

```csharp
"my-category" => new BotProfile
{
    BehaviorTypes = new (string, string?)[]
    {
        ("my-behavior-type", null),
        ("my-aggressive-variant", "harvester")
    }
}
```

### Adjust Timing/Navigation Guidance

Edit `GetTimingGuidance()` and `GetNavigationGuidance()` to change how Ollama generates scenarios for each behavior type.

### Use Different AI Model

Change the model in `GenerateScenarioWithOllamaAsync()`:

```csharp
var chat = new Chat(_ollama, "llama3:8b");  // or any other model
```

## Architecture

```
┌─────────────────┐
│ botdetection.db │  (SQLite database with bot patterns)
└────────┬────────┘
         │ Load categories
         ▼
┌─────────────────────────────────┐
│ BdfScenarioGenerator            │
├─────────────────────────────────┤
│ 1. Load bot categories from DB  │
│ 2. Group by category            │
│ 3. Determine behavior profiles  │
│ 4. Generate Ollama prompts      │
│ 5. Parse JSON responses         │
│ 6. Save BDF scenarios           │
└────────┬────────────────────────┘
         │ Uses Ollama AI
         ▼
┌─────────────────────────────────┐
│ Ollama (mistral-3:8b)           │
├─────────────────────────────────┤
│ Generates realistic BDF JSON    │
│ based on category/behavior type │
└────────┬────────────────────────┘
         │ Output
         ▼
┌─────────────────────────────────┐
│ generated-scenarios/*.json      │
├─────────────────────────────────┤
│ - scraper-aggressive-burst.json │
│ - monitor-periodic-monitor.json │
│ - security-scanner.json         │
│ - ... (all variants)            │
└─────────────────────────────────┘
```

## Performance

- **Generation time:** ~5-10 seconds per scenario (Ollama inference)
- **Total scenarios:** ~15-30 depending on categories in database
- **Total runtime:** ~2-5 minutes for full generation

## Next Steps

After generating scenarios:

1. **Review** generated JSON files for accuracy
2. **Edit** scenarios to fine-tune behavior (optional)
3. **Test** scenarios using `Mostlylucid.BotDetection.BdfRunner`
4. **Integrate** into CI/CD for regression testing
5. **Expand** with custom categories and behavior types

## References

- [BDF System Guide](../Mostlylucid.BotDetection/docs/bdf-system-guide.md)
- [BDF Specification](../Mostlylucid.BotDetection/docs/future/bdf-behaviouralsignature.md)
- [Project Honeypot Test Mode](../Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ProjectHoneypotContributor.cs#L326-L399)
- [Ollama Documentation](https://ollama.ai/docs)
