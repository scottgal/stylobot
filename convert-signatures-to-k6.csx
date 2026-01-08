#!/usr/bin/env dotnet-script
#r "nuget: System.Text.Json, 9.0.0"
#nullable enable

using System.Text.Json;
using System.Text;

/*
 * Converts BDF signatures (bot-signatures/*.json) into k6 load test script
 *
 * Usage:
 *   dotnet script convert-signatures-to-k6.csx -- <bot-signatures-dir> <output-k6-js>
 *
 * Example:
 *   dotnet script convert-signatures-to-k6.csx -- bot-signatures load-test.js
 */

if (Args.Count < 2)
{
    Console.WriteLine("Usage: dotnet script convert-signatures-to-k6.csx -- <bot-signatures-dir> <output-k6-js>");
    Console.WriteLine("Example: dotnet script convert-signatures-to-k6.csx -- bot-signatures load-test.js");
    return;
}

var signaturesDir = Args[0];
var outputFile = Args[1];

if (!Directory.Exists(signaturesDir))
{
    Console.WriteLine($"Error: Directory not found: {signaturesDir}");
    return;
}

Console.WriteLine($"Reading signatures from: {inputFile}");
var lines = File.ReadAllLines(inputFile);
var signatures = new List<SignatureData>();

foreach (var line in lines)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    try
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        // Extract request context
        var result = root.GetProperty("result");
        var requestContext = result.GetProperty("requestContext");
        var multiFactorSig = result.GetProperty("multiFactorSignature");

        var sig = new SignatureData
        {
            Path = requestContext.GetProperty("path").GetString() ?? "/",
            Method = requestContext.GetProperty("method").GetString() ?? "GET",
            Protocol = requestContext.GetProperty("protocol").GetString() ?? "HTTP/1.1",
            HasReferer = requestContext.GetProperty("hasReferer").GetBoolean(),

            // We can't use the actual IP/UA (they're hashed), but we can extract threat info
            ThreatType = result.GetProperty("threatType").GetString() ?? "Unknown",
            ThreatName = result.GetProperty("threatName").GetString() ?? "Unidentified",
            ConfidenceScore = result.GetProperty("confidenceScore").GetDouble(),
            RiskLevel = result.GetProperty("riskLevel").GetString() ?? "Low"
        };

        // Try to extract reasons to understand the bot pattern
        if (result.TryGetProperty("reasons", out var reasons))
        {
            sig.Reasons = reasons.ToString();
        }

        signatures.Add(sig);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Failed to parse signature: {ex.Message}");
    }
}

Console.WriteLine($"Parsed {signatures.Count} signatures");

// Group by threat type to create different scenarios
var botSignatures = signatures.Where(s => s.ConfidenceScore >= 0.5).ToList();
var humanSignatures = signatures.Where(s => s.ConfidenceScore < 0.5).ToList();

Console.WriteLine($"  Bots: {botSignatures.Count}");
Console.WriteLine($"  Humans: {humanSignatures.Count}");

// Generate k6 script
var k6Script = GenerateK6Script(botSignatures, humanSignatures);
File.WriteAllText(outputFile, k6Script);

Console.WriteLine($"Generated k6 script: {outputFile}");
Console.WriteLine($"\nTo run the load test:");
Console.WriteLine($"  k6 run {outputFile}");
Console.WriteLine($"\nOr with custom VUs and duration:");
Console.WriteLine($"  k6 run --vus 10 --duration 30s {outputFile}");

// Data structure
class SignatureData
{
    public string Path { get; set; } = "/";
    public string Method { get; set; } = "GET";
    public string Protocol { get; set; } = "HTTP/1.1";
    public bool HasReferer { get; set; }
    public string ThreatType { get; set; } = "Unknown";
    public string ThreatName { get; set; } = "Unidentified";
    public double ConfidenceScore { get; set; }
    public string RiskLevel { get; set; } = "Low";
    public string? Reasons { get; set; }
}

string GenerateK6Script(List<SignatureData> bots, List<SignatureData> humans)
{
    var sb = new StringBuilder();

    sb.AppendLine("""
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';

// Custom metrics
const botRequests = new Counter('bot_requests');
const humanRequests = new Counter('human_requests');
const detectionRate = new Rate('detection_rate');

// Load test configuration
export const options = {
    stages: [
        { duration: '30s', target: 10 },  // Ramp up to 10 VUs
        { duration: '1m', target: 10 },   // Stay at 10 VUs
        { duration: '10s', target: 0 },   // Ramp down
    ],
    thresholds: {
        http_req_duration: ['p(95)<500'], // 95% of requests should be below 500ms
        http_req_failed: ['rate<0.1'],    // Less than 10% requests should fail
    },
};

// Gateway URL (bot detection proxy)
const GATEWAY_URL = 'http://localhost:5000';

""");

    // Generate bot patterns
    sb.AppendLine("// Bot patterns from signatures");
    sb.AppendLine("const botPatterns = [");
    foreach (var bot in bots.Take(50)) // Limit to 50 patterns
    {
        var ua = GetUserAgentForThreat(bot.ThreatType, bot.ThreatName);
        sb.AppendLine($"    {{");
        sb.AppendLine($"        path: '{EscapeJs(bot.Path)}',");
        sb.AppendLine($"        method: '{bot.Method}',");
        sb.AppendLine($"        userAgent: '{EscapeJs(ua)}',");
        sb.AppendLine($"        threatType: '{EscapeJs(bot.ThreatType)}',");
        sb.AppendLine($"        expectedDetection: true,");
        sb.AppendLine($"        confidenceScore: {bot.ConfidenceScore:F2}");
        sb.AppendLine($"    }},");
    }
    sb.AppendLine("];");
    sb.AppendLine();

    // Generate human patterns
    sb.AppendLine("// Human patterns from signatures");
    sb.AppendLine("const humanPatterns = [");
    foreach (var human in humans.Take(50)) // Limit to 50 patterns
    {
        var ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        sb.AppendLine($"    {{");
        sb.AppendLine($"        path: '{EscapeJs(human.Path)}',");
        sb.AppendLine($"        method: '{human.Method}',");
        sb.AppendLine($"        userAgent: '{EscapeJs(ua)}',");
        sb.AppendLine($"        threatType: 'Human',");
        sb.AppendLine($"        expectedDetection: false,");
        sb.AppendLine($"        confidenceScore: {human.ConfidenceScore:F2}");
        sb.AppendLine($"    }},");
    }
    sb.AppendLine("];");
    sb.AppendLine();

    // Main test function
    sb.AppendLine("""
// Main test function - called for each VU iteration
export default function() {
    // Choose between bot and human pattern
    // If we have both: 70% human, 30% bot (realistic traffic mix)
    // If we only have bots: 100% bot
    let isBot, patterns;
    if (humanPatterns.length > 0 && botPatterns.length > 0) {
        isBot = Math.random() < 0.3;
        patterns = isBot ? botPatterns : humanPatterns;
    } else if (humanPatterns.length > 0) {
        isBot = false;
        patterns = humanPatterns;
    } else {
        isBot = true;
        patterns = botPatterns;
    }

    const pattern = patterns[Math.floor(Math.random() * patterns.length)];

    // Build request
    const url = `${GATEWAY_URL}${pattern.path}`;
    const params = {
        headers: {
            'User-Agent': pattern.userAgent,
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
            'Accept-Language': 'en-US,en;q=0.9',
            'Accept-Encoding': 'gzip, deflate',
            'Connection': 'keep-alive',
        },
        tags: {
            pattern_type: isBot ? 'bot' : 'human',
            threat_type: pattern.threatType,
            expected_score: pattern.confidenceScore
        }
    };

    // Make request
    const res = http.get(url, params);

    // Check response
    const success = check(res, {
        'status is 200 or 403': (r) => r.status === 200 || r.status === 403,
        'response has bot detection header': (r) => r.headers['X-Bot-Detection'] !== undefined,
    });

    // Track metrics
    if (isBot) {
        botRequests.add(1);
    } else {
        humanRequests.add(1);
    }

    // Check if bot was detected
    const detectedAsBot = res.headers['X-Bot-Detection'] === 'True' || res.status === 403;
    detectionRate.add(detectedAsBot ? 1 : 0);

    // Log interesting cases
    if (pattern.expectedDetection && !detectedAsBot) {
        console.log(`False negative: ${pattern.threatType} not detected (score: ${pattern.confidenceScore})`);
    }
    if (!pattern.expectedDetection && detectedAsBot) {
        console.log(`False positive: Human detected as bot`);
    }

    // Realistic pacing - humans are slower, bots are faster
    sleep(isBot ? Math.random() * 0.5 : Math.random() * 2 + 1);
}

// Setup function - runs once before test
export function setup() {
    console.log('Starting load test against bot detection gateway');
    console.log(`Gateway URL: ${GATEWAY_URL}`);
    console.log(`Bot patterns: ${botPatterns.length}`);
    console.log(`Human patterns: ${humanPatterns.length}`);
    return {};
}

// Teardown function - runs once after test
export function teardown(data) {
    console.log('Load test completed');
}
""");

    return sb.ToString();
}

string GetUserAgentForThreat(string threatType, string threatName)
{
    // Generate appropriate user agent based on threat type
    return (threatType, threatName.ToLower()) switch
    {
        ("Scraper", var name) when name.Contains("curl") => "curl/7.68.0",
        ("Scraper", var name) when name.Contains("wget") => "Wget/1.20.3 (linux-gnu)",
        ("Scraper", var name) when name.Contains("python") => "python-requests/2.28.0",
        ("Scraper", var name) when name.Contains("scrapy") => "Scrapy/2.8.0 (+https://scrapy.org)",
        ("Scraper", var name) when name.Contains("selenium") => "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36",
        ("MaliciousBot", _) => "Mozilla/5.0 (compatible; BadBot/1.0; +http://badbot.com/bot.html)",
        _ => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
    };
}

string EscapeJs(string text)
{
    if (string.IsNullOrEmpty(text)) return text;
    return text.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "\\r");
}
