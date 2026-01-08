#!/usr/bin/env dotnet-script
#r "nuget: OllamaSharp, 5.4.12"
#nullable enable

using OllamaSharp;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.IO;

Console.WriteLine("=".PadRight(80, '='));
Console.WriteLine("COMPREHENSIVE BOT DETECTION SIGNATURE GENERATOR");
Console.WriteLine("Using QWEN2.5-CODER:3B for temporal behavior generation");
Console.WriteLine("Creating signature repository structure...");
Console.WriteLine("=".PadRight(80, '='));
Console.WriteLine();

var ollama = new OllamaApiClient("http://localhost:11434")
{
    SelectedModel = "qwen2.5-coder:3b"
};

// Real datacenter IP prefixes from our IpContributor
var datacenterIpPrefixes = new (string Name, string[] Prefixes)[]
{
    ("AWS", new[] {"3.", "13.", "18.", "35.", "52.", "54."}),
    ("Google Cloud", new[] {"34.", "35."}),
    ("Azure", new[] {"13.", "20.", "40.", "52."}),
    ("DigitalOcean", new[] {"104.131.", "104.236.", "159.65.", "167.99."}),
    ("Linode", new[] {"45.33.", "45.56.", "45.79."}),
    ("Vultr", new[] {"45.32.", "45.63.", "45.76.", "45.77."}),
    ("OVH", new[] {"51.38.", "51.68.", "51.77.", "51.91."}),
    ("Hetzner", new[] {"65.21.", "95.216.", "135.181.", "168.119."})
};

// Real bot User-Agents from our UserAgentContributor
var botUserAgents = new (string Pattern, string Name)[]
{
    ("curl/8.4.0", "curl"),
    ("wget/1.21.4", "wget"),
    ("python-requests/2.31.0", "python-requests"),
    ("python-urllib/3.11", "python-urllib"),
    ("Scrapy/2.11.0 (+https://scrapy.org)", "Scrapy"),
    ("SeleniumHQ/4.15.0 (Headless; Chrome/120.0)", "Selenium"),
    ("HeadlessChrome/120.0.6099.109", "Headless browser"),
    ("PhantomJS/2.1.1", "PhantomJS"),
    ("Puppeteer/21.6.0", "Puppeteer"),
    ("Playwright/1.40.0 (node)", "Playwright"),
    ("HTTrack/3.49", "HTTrack"),
    ("libwww-perl/6.67", "libwww-perl"),
    ("Java/17.0.9", "Java HTTP client"),
    ("Apache-HttpClient/4.5.14 (Java/17.0.9)", "Apache HttpClient"),
    ("okhttp/4.12.0", "OkHttp"),
    ("Go-http-client/2.0", "Go HTTP client"),
    ("node-fetch/2.7.0 (+https://github.com/node-fetch/node-fetch)", "node-fetch"),
    ("axios/1.6.2", "axios")
};

// Good human User-Agents for contrast
var humanUserAgents = new[]
{
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
    "Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1",
    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0"
};

// Good residential IP ranges (not datacenter)
var residentialIpPrefixes = new[]
{
    "90.196.", // European residential
    "73.158.", // US residential (Comcast)
    "86.144.", // UK residential (BT)
    "203.221.", // Australian residential
    "201.151.", // South American residential
    "180.149." // Asian residential
};

var random = new Random();

// Create signature repository directory (in current directory)
var repoPath = Path.Combine(Directory.GetCurrentDirectory(), "bot-signatures");
Directory.CreateDirectory(repoPath);

Console.WriteLine($"📁 Repository path: {repoPath}");
Console.WriteLine();

// All the detection dimensions we need to cover
var behaviorCategories = new Dictionary<string, string[]>
{
    ["request-timing"] = new[]
    {
        "Time between requests (human: 2-30s, bot: <1s or exactly timed)",
        "Request bursts vs steady flow",
        "Day/night patterns (humans sleep)",
        "Weekend vs weekday patterns",
        "Session duration (human: 2-15min, bot: hours)"
    },

    ["path-patterns"] = new[]
    {
        "Navigation sequence (human: homepage → browse → detail, bot: direct deep links)",
        "Backtracking and revisits (humans go back, bots rarely do)",
        "Honeypot links (invisible traps only bots click)",
        "Robots.txt violations",
        "Sitemap.xml crawling patterns",
        "Sequential ID scraping (user/1, user/2, user/3...)",
        "Static asset patterns (CSS/JS requests matching browser)",
        "Search crawling (Google: follows links, respects crawl-delay)"
    },

    ["headers"] = new[]
    {
        "User-Agent consistency with other headers",
        "Accept headers matching claimed browser",
        "Accept-Language (human: 1-3 languages, bot: missing or 'en-US,en;q=0.9')",
        "Accept-Encoding (gzip, deflate, br for modern browsers)",
        "Referer chains (logical navigation path)",
        "DNT (Do Not Track) header presence",
        "Sec-Fetch-* headers (modern browsers)",
        "Upgrade-Insecure-Requests header",
        "Cookie handling (humans accept, bots often don't)",
        "Connection keep-alive patterns",
        "TE (Transfer-Encoding) header",
        "HTTP version (HTTP/1.1, HTTP/2, HTTP/3)"
    },

    ["user-agent"] = new[]
    {
        "Browser version age (human: current -2 months, bot: outdated or too new)",
        "OS version consistency",
        "Device type consistency (desktop/mobile/tablet)",
        "Rendering engine match (Chrome=Chromium, Safari=WebKit)",
        "Known bot signatures (Googlebot, curl, python-requests)",
        "Generic/minimal UAs (missing detail)",
        "Rare browser/OS combinations"
    },

    ["client-side"] = new[]
    {
        "JavaScript execution (Canvas fingerprint, WebGL)",
        "Screen resolution and color depth",
        "Timezone vs IP geolocation match",
        "Browser plugins (PDF, Flash - though Flash is dead)",
        "Do Not Track setting",
        "Cookie enabled status",
        "Local storage available",
        "Session storage available",
        "IndexedDB support",
        "WebRTC leak detection",
        "Battery API (mobile)",
        "Device memory, hardware concurrency",
        "Touch support matching device type"
    },

    ["ip-network"] = new[]
    {
        "IP geolocation vs Accept-Language match",
        "Datacenter IP ranges (AWS, Azure, DigitalOcean)",
        "VPN/Proxy detection (Project Honeypot, IPQualityScore)",
        "IP reputation (spam lists, bot lists)",
        "ASN (Autonomous System Number) - hosting vs residential",
        "IP rotation patterns (bots rotate, humans don't)",
        "IPv4 vs IPv6 usage patterns",
        "TLS fingerprint (JA3 hash - unique per client)",
        "HTTP/2 fingerprint (SETTINGS frame order)",
        "TCP/IP stack fingerprint (TTL, window size, options)"
    },

    ["behavioral"] = new[]
    {
        "Mouse movements (human: curves, bot: straight lines or missing)",
        "Keystroke dynamics (timing between keys)",
        "Scroll patterns (smooth human scroll vs instant jump)",
        "Focus/blur events (tab switching)",
        "Page visibility changes",
        "Copy/paste vs typing",
        "Right-click context menu usage",
        "Form interaction patterns",
        "Time on page before interaction",
        "Rage clicks (frustrated humans)",
        "Dead clicks (clicks on non-interactive elements)",
        "Hover patterns before clicking"
    },

    ["cache-state"] = new[]
    {
        "If-Modified-Since header usage",
        "ETag validation",
        "Cache-Control respect",
        "Cookie persistence across sessions",
        "Session cookie vs persistent cookie handling",
        "Third-party cookie blocking (privacy-conscious users)",
        "Local storage data from previous visits",
        "Browser cache hit ratios"
    },

    ["response-behavior"] = new[]
    {
        "Handling of redirects (3xx codes)",
        "Response to 404s (humans stop, bots continue)",
        "Response to rate limiting (429)",
        "Response to CAPTCHAs",
        "Response to JavaScript challenges",
        "Content-Type header respect (not requesting images as HTML)",
        "Following meta refresh directives",
        "Handling of HTTP errors gracefully"
    },

    ["content-interaction"] = new[]
    {
        "Reading time vs content length (humans read, bots scrape instantly)",
        "Scrolling depth (do they read the whole page?)",
        "Link click patterns (which links are most interesting)",
        "Video/audio playback indicators",
        "Download patterns (PDFs, documents)",
        "Search query patterns (human: typos, refinement; bot: perfect queries)",
        "Form submission patterns (human: corrections, bot: one-shot)",
        "Image loading patterns (lazy loading triggers)"
    }
};

Console.WriteLine($"Total behavior categories: {behaviorCategories.Count}");
Console.WriteLine($"Total behavior signals: {behaviorCategories.Values.Sum(v => v.Length)}");
Console.WriteLine();

// Generate README for the repository
var readme = $@"# Bot Detection Signature Repository

**Generated:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
**Model:** ministral-3:3b
**Version:** 1.0.0

## Overview

Each file in this directory is a complete behavioral signature that can be replayed to test bot detection.

## File Format

Each scenario file (e.g., `natural-browsing.json`, `rapid-scraper.json`) contains:
- **scenarioName**: Kebab-case identifier
- **scenario**: Human-readable description
- **confidence**: Bot confidence score (0.0-1.0)
- **requests**: Array of sequential HTTP requests with timing
- **patterns**: Observable behavioral patterns
- **reasoning**: Why this is bot/human behavior

## Usage

Use `stylobot.bdfreplay.cli` to replay these signatures:
```
stylobot.bdfreplay.cli --signature natural-browsing.json --target http://localhost:5000
```

## License

Generated for bot detection research and development.
";

File.WriteAllText(Path.Combine(repoPath, "README.md"), readme);

Console.WriteLine("✅ Repository structure created");
Console.WriteLine();

// Generate multiple scenarios for each category
var generatedCount = 0;
var scenariosPerCategory = 5; // Generate 5 human + 5 bot per category = 100 total signatures

foreach (var category in behaviorCategories)
{
    Console.WriteLine($"⚙️  Generating scenarios for: {category.Key}");
    Console.WriteLine();

    // Generate multiple human scenarios
    for (int i = 0; i < scenariosPerCategory; i++)
    {
        var scenarioJson = await GenerateScenario(ollama, category.Key, category.Value, isBot: false);
        var scenarioData = SaveScenario(scenarioJson, repoPath, category.Key, "human");
        if (scenarioData != null)
        {
            Console.WriteLine($"  ✅ Saved: {scenarioData}");
            generatedCount++;
        }
    }

    // Generate multiple bot scenarios
    for (int i = 0; i < scenariosPerCategory; i++)
    {
        var scenarioJson = await GenerateScenario(ollama, category.Key, category.Value, isBot: true);
        var scenarioData = SaveScenario(scenarioJson, repoPath, category.Key, "bot");
        if (scenarioData != null)
        {
            Console.WriteLine($"  ✅ Saved: {scenarioData}");
            generatedCount++;
        }
    }

    Console.WriteLine();
}

string? SaveScenario(string jsonText, string repoPath, string category, string type)
{
    try
    {
        // Validate it's parseable JSON first
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonText);
        }
        catch (JsonException jex)
        {
            Console.WriteLine($"  ❌ Invalid JSON: {jex.Message}");
            Console.WriteLine($"     First 200 chars: {jsonText.Substring(0, Math.Min(200, jsonText.Length))}");
            return null;
        }

        // Extract scenarioName
        if (!doc.RootElement.TryGetProperty("scenarioName", out var scenarioNameProp))
        {
            Console.WriteLine($"  ⚠️  No scenarioName in JSON, skipping");
            doc.Dispose();
            return null;
        }

        var scenarioName = scenarioNameProp.GetString();
        if (string.IsNullOrEmpty(scenarioName))
        {
            Console.WriteLine($"  ⚠️  Empty scenarioName, skipping");
            doc.Dispose();
            return null;
        }

        // Save with pretty formatting
        var filename = $"{scenarioName}.json";
        var filepath = Path.Combine(repoPath, filename);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var prettyJson = JsonSerializer.Serialize(doc.RootElement, options);

        File.WriteAllText(filepath, prettyJson);
        doc.Dispose();
        return filename;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ❌ Error saving scenario: {ex.Message}");
        return null;
    }
}

Console.WriteLine();
Console.WriteLine("=".PadRight(80, '='));
Console.WriteLine($"✅ Generated {generatedCount} scenario files");
Console.WriteLine($"📁 Repository: {repoPath}");
Console.WriteLine($"📊 Categories covered: {behaviorCategories.Count}");
Console.WriteLine($"🎭 Scenarios per category: {scenariosPerCategory} human + {scenariosPerCategory} bot");
Console.WriteLine();
Console.WriteLine("Use these signatures for training and testing:");
Console.WriteLine("  stylobot.bdfreplay.cli --signature <scenario-name>.json --target http://localhost:5000");
Console.WriteLine("=".PadRight(80, '='));

async Task<string> GenerateScenario(OllamaApiClient client, string category, string[] signals, bool isBot)
{
    var signalsList = string.Join("\n   - ", signals);
    var userType = isBot ? "bot/scraper" : "real human user";

    // Select random real values for bot scenarios
    var selectedBotUA = botUserAgents[random.Next(botUserAgents.Length)];
    var selectedDatacenter = datacenterIpPrefixes[random.Next(datacenterIpPrefixes.Length)];
    var selectedDcIP = $"{selectedDatacenter.Prefixes[random.Next(selectedDatacenter.Prefixes.Length)]}{random.Next(1, 254)}.{random.Next(1, 254)}";

    // Select random real values for human scenarios
    var selectedHumanUA = humanUserAgents[random.Next(humanUserAgents.Length)];
    var selectedResidentialPrefix = residentialIpPrefixes[random.Next(residentialIpPrefixes.Length)];
    var selectedResidentialIP = $"{selectedResidentialPrefix}{random.Next(1, 254)}.{random.Next(1, 254)}";

    var ua = isBot ? selectedBotUA.Pattern : selectedHumanUA;
    var ip = isBot ? selectedDcIP : selectedResidentialIP;

    var prompt = $@"Generate ONE realistic bot detection signature (BDF v2) for: {userType} - {category}

USER-AGENT (use EXACTLY in clientProfile.userAgent): {ua}

Create a realistic {(isBot ? "scraper/bot" : "human")} with:

BDF v2 Schema:
{{
  ""scenarioName"": ""kebab-case-name"",
  ""scenario"": ""description"",
  ""confidence"": {(isBot ? "0.85" : "0.15")},
  ""clientProfile"": {{
    ""userAgent"": ""{ua}"",
    ""cookieMode"": ""{(isBot ? "none" : "sticky")}"",
    ""headerCompleteness"": ""{(isBot ? "minimal" : "full")}"",
    ""clientHintsPresent"": {(isBot ? "false" : "true")},
    ""robotsConsulted"": {(isBot ? "false" : "true")}
  }},
  ""timingProfile"": {{
    ""burstRequests"": {(isBot ? "10" : "3")},
    ""delayAfterMs"": {{ ""min"": {(isBot ? "20" : "2000")}, ""max"": {(isBot ? "150" : "15000")} }},
    ""pauseAfterBurstMs"": {{ ""min"": {(isBot ? "500" : "5000")}, ""max"": {(isBot ? "2000" : "20000")} }}
  }},
  ""requests"": [
    {{
      ""method"": ""GET"",
      ""path"": ""/"",
      ""headers"": {{""User-Agent"": ""{ua}""{(isBot ? "" : ", \"Accept-Language\": \"en-US,en;q=0.9\"")}}},
      ""expectedStatusAny"": [200, 301, 302],
      ""expectedOutcome"": ""{(isBot ? "indexing" : "browsing")}"",
      ""successCondition"": ""any 2xx""
    }}
  ],
  ""labels"": [{(isBot ? "\"Scraper\", \"RobotsIgnore\"" : "\"Human\", \"NaturalBrowsing\"")}],
  ""evidence"": [
    {{ ""signal"": ""interval_ms_p95"", ""op"": ""{(isBot ? "<" : ">")}"", ""value"": {(isBot ? "200" : "3000")}, ""weight"": 0.35 }}
  ],
  ""patterns"": {{""requestInterval"": ""{(isBot ? "burst <150ms" : "variable 2-15s")}""}},
  ""reasoning"": ""Why {userType}""
}}

CRITICAL for BOTS:
- Include robots.txt check (or skip it)
- Hit /admin, /api, sensitive paths
- Missing Accept-Language, Sec-Fetch-* headers
- Use HEAD before GET sometimes
- Enumerate: /api/data?page=1, /api/data?page=2
- Include 403/404/429 in expectedStatusAny

CRITICAL for HUMANS:
- Full browser headers
- Cookie jar (sticky)
- Natural navigation
- Referer chains
- Variable timing 2-30s

Return ONLY valid JSON, no markdown, no comments, no trailing commas.";

    try
    {
        Console.Write($"  ⏳ Calling Ollama ({userType})...");
        var chat = new Chat(client);
        var response = new StringBuilder();
        var tokenCount = 0;

        await foreach (var token in chat.SendAsync(prompt, CancellationToken.None))
        {
            response.Append(token);
            tokenCount++;
            if (tokenCount % 50 == 0)
            {
                Console.Write(".");
            }
        }

        Console.WriteLine($" ✓ ({tokenCount} tokens)");

        // Clean up response: strip markdown code fences and fix common JSON issues
        var jsonText = response.ToString().Trim();

        // Remove markdown code fences
        if (jsonText.StartsWith("```json"))
        {
            jsonText = jsonText.Substring(7);
        }
        if (jsonText.StartsWith("```"))
        {
            jsonText = jsonText.Substring(3);
        }
        if (jsonText.EndsWith("```"))
        {
            jsonText = jsonText.Substring(0, jsonText.Length - 3);
        }

        jsonText = jsonText.Trim();

        // Fix literal \n characters outside of string values
        jsonText = System.Text.RegularExpressions.Regex.Replace(
            jsonText,
            @"(""[^""]*""\s*[,:\[\{]\s*)\\n(\s+)",
            "$1\n$2"
        );

        // Remove comments (// and /* */)
        jsonText = System.Text.RegularExpressions.Regex.Replace(
            jsonText,
            @"//.*$",
            "",
            System.Text.RegularExpressions.RegexOptions.Multiline
        );
        jsonText = System.Text.RegularExpressions.Regex.Replace(
            jsonText,
            @"/\*.*?\*/",
            "",
            System.Text.RegularExpressions.RegexOptions.Singleline
        );

        // Fix trailing commas before closing braces/brackets
        jsonText = System.Text.RegularExpressions.Regex.Replace(
            jsonText,
            @",(\s*[}\]])",
            "$1"
        );

        return jsonText;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ❌ Error generating scenario: {ex.Message}");
        return $"{{\"error\": \"{ex.Message}\"}}";
    }
}
