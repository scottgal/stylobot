#!/usr/bin/env dotnet-script
#r "nuget: System.Text.Json, 9.0.0"
#nullable enable

using System.Text.Json;
using System.Text;

/*
 * Converts BDF signatures (bot-signatures/*.json) into k6 load test script
 *
 * Usage:
 *   dotnet script convert-bdf-to-k6.csx -- <bot-signatures-dir> <output-k6-js>
 *
 * Example:
 *   dotnet script convert-bdf-to-k6.csx -- bot-signatures load-test.js
 */

if (Args.Count < 2)
{
    Console.WriteLine("Usage: dotnet script convert-bdf-to-k6.csx -- <bot-signatures-dir> <output-k6-js>");
    Console.WriteLine("Example: dotnet script convert-bdf-to-k6.csx -- bot-signatures load-test.js");
    return;
}

var signaturesDir = Args[0];
var outputFile = Args[1];

if (!Directory.Exists(signaturesDir))
{
    Console.WriteLine($"Error: Directory not found: {signaturesDir}");
    return;
}

Console.WriteLine($"Reading BDF signatures from: {signaturesDir}");

var signatureFiles = Directory.GetFiles(signaturesDir, "*.json")
    .Where(f => !f.EndsWith("README.md"))
    .ToList();

Console.WriteLine($"Found {signatureFiles.Count} signature files");

// Load all signatures
var signatures = new List<BdfSignature>();
foreach (var file in signatureFiles)
{
    try
    {
        var json = File.ReadAllText(file);
        var sig = JsonSerializer.Deserialize<BdfSignature>(json);
        if (sig != null)
        {
            signatures.Add(sig);
            Console.WriteLine($"  ✓ {Path.GetFileName(file)}: {sig.scenario}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ {Path.GetFileName(file)}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"Loaded {signatures.Count} valid signatures");

if (signatures.Count == 0)
{
    Console.WriteLine("No valid signatures found!");
    return;
}

// Generate k6 script
var k6Script = GenerateK6Script(signatures);
File.WriteAllText(outputFile, k6Script);

Console.WriteLine($"Generated k6 script: {outputFile}");
Console.WriteLine();
Console.WriteLine("To run:");
Console.WriteLine($"  k6 run {outputFile}");
Console.WriteLine();
Console.WriteLine("With custom settings:");
Console.WriteLine($"  k6 run --vus 20 --duration 60s {outputFile}");

// Data model
class BdfSignature
{
    public string? scenarioName { get; set; }
    public string? scenario { get; set; }
    public double confidence { get; set; }
    public BdfRequest[]? requests { get; set; }
    public JsonElement patterns { get; set; }
    public string? reasoning { get; set; }
}

class BdfRequest
{
    public double timestamp { get; set; }
    public string? method { get; set; }
    public string? path { get; set; }
    public Dictionary<string, string>? headers { get; set; }
    public int expectedStatus { get; set; }
    public double delayAfter { get; set; }
}

string GenerateK6Script(List<BdfSignature> signatures)
{
    var sb = new StringBuilder();

    // Header
    sb.AppendLine(@"import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

// Custom metrics
const totalRequests = new Counter('total_requests');
const botScenarios = new Counter('bot_scenarios');
const humanScenarios = new Counter('human_scenarios');
const detectionRate = new Rate('detection_rate');
const scenarioDuration = new Trend('scenario_duration');

// Load test configuration
export const options = {
    stages: [
        { duration: '30s', target: 10 },  // Ramp up to 10 VUs
        { duration: '2m', target: 10 },   // Stay at 10 VUs
        { duration: '30s', target: 0 },   // Ramp down
    ],
    thresholds: {
        http_req_duration: ['p(95)<1000'],     // 95% of requests < 1s
        http_req_failed: ['rate<0.1'],         // Less than 10% failures
        'detection_rate': ['rate>0.3'],        // At least 30% detected as bots
    },
};

// Target URL (TestSite runs on 7777)
const TARGET_URL = __ENV.TARGET_URL || 'http://localhost:7777';

");

    // Embed all signatures
    sb.AppendLine("// Embedded BDF signatures");
    sb.AppendLine("const signatures = [");

    foreach (var sig in signatures)
    {
        sb.AppendLine("  {");
        sb.AppendLine($"    scenarioName: '{EscapeJs(sig.scenarioName ?? "unknown")}',");
        sb.AppendLine($"    scenario: '{EscapeJs(sig.scenario ?? "")}',");
        sb.AppendLine($"    confidence: {sig.confidence:F2},");
        sb.AppendLine($"    isBot: {(sig.confidence > 0.5 ? "true" : "false")},");
        sb.AppendLine("    requests: [");

        if (sig.requests != null)
        {
            foreach (var req in sig.requests)
            {
                sb.AppendLine("      {");
                sb.AppendLine($"        timestamp: {req.timestamp},");
                sb.AppendLine($"        method: '{req.method}',");
                sb.AppendLine($"        path: '{EscapeJs(req.path ?? "/")}',");
                sb.Append("        headers: {");
                if (req.headers != null)
                {
                    sb.Append(string.Join(", ", req.headers.Select(h => $"'{h.Key}': '{EscapeJs(h.Value)}'")));
                }
                sb.AppendLine("},");
                sb.AppendLine($"        expectedStatus: {req.expectedStatus},");
                sb.AppendLine($"        delayAfter: {req.delayAfter}");
                sb.AppendLine("      },");
            }
        }

        sb.AppendLine("    ]");
        sb.AppendLine("  },");
    }

    sb.AppendLine("];");
    sb.AppendLine();

    // Main test function
    sb.AppendLine(@"
// Main test function - each VU iteration picks a random signature and replays it
export default function() {
    const scenarioStart = Date.now();

    // Pick a random signature
    const sig = signatures[Math.floor(Math.random() * signatures.length)];

    // Track bot vs human scenarios
    if (sig.isBot) {
        botScenarios.add(1);
    } else {
        humanScenarios.add(1);
    }

    console.log(`[VU ${__VU}] Playing: ${sig.scenarioName} (confidence: ${sig.confidence})`);

    let detectedAsBot = false;

    // Replay all requests in the scenario
    for (let i = 0; i < sig.requests.length; i++) {
        const req = sig.requests[i];

        // Build full URL
        const url = `${TARGET_URL}${req.path}`;

        // Prepare headers
        const params = {
            headers: {
                'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
                'Accept-Language': 'en-US,en;q=0.9',
                'Accept-Encoding': 'gzip, deflate',
                'Connection': 'keep-alive',
                ...req.headers
            },
            tags: {
                scenario: sig.scenarioName,
                scenario_type: sig.isBot ? 'bot' : 'human',
                request_index: i,
                expected_confidence: sig.confidence
            }
        };

        // Make request
        const res = http.request(req.method, url, null, params);
        totalRequests.add(1);

        // Check response
        check(res, {
            'status is expected or blocked': (r) => r.status === req.expectedStatus || r.status === 403 || r.status === 200,
            'has bot detection header': (r) => r.headers['X-Bot-Detection'] !== undefined,
        });

        // Track if detected as bot
        if (res.headers['X-Bot-Detection'] === 'True' || res.status === 403) {
            detectedAsBot = true;
        }

        // Wait before next request (as specified in signature)
        if (req.delayAfter > 0) {
            sleep(req.delayAfter);
        }
    }

    // Record detection accuracy
    detectionRate.add(detectedAsBot ? 1 : 0);

    // Log interesting cases
    if (sig.isBot && !detectedAsBot) {
        console.log(`❌ False negative: ${sig.scenarioName} not detected (confidence: ${sig.confidence})`);
    }
    if (!sig.isBot && detectedAsBot) {
        console.log(`⚠️  False positive: ${sig.scenarioName} detected as bot (confidence: ${sig.confidence})`);
    }

    // Track scenario duration
    const duration = (Date.now() - scenarioStart) / 1000;
    scenarioDuration.add(duration);
}

// Setup
export function setup() {
    console.log('================================================================================');
    console.log('BDF Signature Replay - k6 Load Test');
    console.log('================================================================================');
    console.log(`Target URL: ${TARGET_URL}`);
    console.log(`Loaded signatures: ${signatures.length}`);
    console.log(`  - Bot scenarios: ${signatures.filter(s => s.isBot).length}`);
    console.log(`  - Human scenarios: ${signatures.filter(s => !s.isBot).length}`);
    console.log('================================================================================');
    console.log('');
    return {};
}

// Teardown
export function teardown(data) {
    console.log('');
    console.log('================================================================================');
    console.log('Load test completed');
    console.log('================================================================================');
}
");

    return sb.ToString();
}

string EscapeJs(string text)
{
    if (string.IsNullOrEmpty(text)) return text;
    return text
        .Replace("\\", "\\\\")
        .Replace("'", "\\'")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r")
        .Replace("\t", "\\t");
}
