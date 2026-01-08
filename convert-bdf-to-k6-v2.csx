#!/usr/bin/env dotnet-script
#r "nuget: System.Text.Json, 9.0.0"
#nullable enable

using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;

/*
 * Converts BDF v2 signatures into k6 load test script with realistic interleaving
 *
 * Usage:
 *   dotnet script convert-bdf-to-k6-v2.csx -- <bot-signatures-dir> <output-k6-js>
 *
 * Example:
 *   dotnet script convert-bdf-to-k6-v2.csx -- bot-signatures bdf-load-test-v2.js
 */

if (Args.Count < 2)
{
    Console.WriteLine("Usage: dotnet script convert-bdf-to-k6-v2.csx -- <bot-signatures-dir> <output-k6-js>");
    Console.WriteLine("Example: dotnet script convert-bdf-to-k6-v2.csx -- bot-signatures bdf-load-test-v2.js");
    return;
}

var signaturesDir = Args[0];
var outputFile = Args[1];

if (!Directory.Exists(signaturesDir))
{
    Console.WriteLine($"Error: Directory not found: {signaturesDir}");
    return;
}

Console.WriteLine($"Reading BDF v2 signatures from: {signaturesDir}");

var signatureFiles = Directory.GetFiles(signaturesDir, "*.json")
    .Where(f => !f.EndsWith("README.md"))
    .ToList();

Console.WriteLine($"Found {signatureFiles.Count} signature files");

// Load all signatures
var signatures = new List<BdfSignatureV2>();
foreach (var file in signatureFiles)
{
    try
    {
        var json = File.ReadAllText(file);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var sig = JsonSerializer.Deserialize<BdfSignatureV2>(json, options);
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
var k6Script = GenerateK6ScriptV2(signatures);
File.WriteAllText(outputFile, k6Script);

Console.WriteLine($"Generated k6 script: {outputFile}");
Console.WriteLine();
Console.WriteLine("To run:");
Console.WriteLine($"  k6 run {outputFile}");
Console.WriteLine();
Console.WriteLine("With custom settings:");
Console.WriteLine($"  k6 run --vus 20 --duration 60s {outputFile}");

// BDF v2 Data Model
class BdfSignatureV2
{
    public string? scenarioName { get; set; }
    public string? scenario { get; set; }
    public double confidence { get; set; }
    public ClientProfile? clientProfile { get; set; }
    public TimingProfile? timingProfile { get; set; }
    public BdfRequestV2[]? requests { get; set; }
    public Dictionary<string, string>? patterns { get; set; }
    public string[]? labels { get; set; }
    public Evidence[]? evidence { get; set; }
    public string? reasoning { get; set; }
}

class ClientProfile
{
    public string? userAgent { get; set; }
    public string? cookieMode { get; set; } // "none" | "stateless" | "sticky"
    public string? headerCompleteness { get; set; } // "minimal" | "partial" | "full"
    public bool clientHintsPresent { get; set; }
    public bool robotsConsulted { get; set; }
    public string? protocol { get; set; }
}

class TimingProfile
{
    public int burstRequests { get; set; }
    public MinMax? delayAfterMs { get; set; }
    public MinMax? pauseAfterBurstMs { get; set; }
}

class MinMax
{
    public int min { get; set; }
    public int max { get; set; }
}

class BdfRequestV2
{
    public string? method { get; set; }
    public string? path { get; set; }
    public Dictionary<string, string>? headers { get; set; }
    public int[]? expectedStatusAny { get; set; }
    public string? expectedOutcome { get; set; }
    public string? successCondition { get; set; }
    public string? note { get; set; }
}

class Evidence
{
    public string? signal { get; set; }
    public string? op { get; set; }
    public double value { get; set; }
    public double weight { get; set; }
}

string GenerateK6ScriptV2(List<BdfSignatureV2> signatures)
{
    var sb = new StringBuilder();

    // Header with helper functions
    sb.AppendLine(@"import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';

// Custom metrics for evidence tracking
const totalRequests = new Counter('total_requests');
const botScenarios = new Counter('bot_scenarios');
const humanScenarios = new Counter('human_scenarios');
const detectionRate = new Rate('detection_rate');
const scenarioDuration = new Trend('scenario_duration');
const intervalTrend = new Trend('interval_ms');
const sensitivePaths = new Rate('sensitive_path_rate');
const burstRate = new Rate('burst_detected');

// Load test configuration - multiple VUs provide natural interleaving
export const options = {
    stages: [
        { duration: '30s', target: 10 },  // Ramp up to 10 VUs (interleaved requests)
        { duration: '2m', target: 10 },   // Stay at 10 VUs
        { duration: '30s', target: 0 },   // Ramp down
    ],
    thresholds: {
        http_req_duration: ['p(95)<1000'],
        http_req_failed: ['rate<0.1'],
        'detection_rate': ['rate>0.3'],
    },
};

// Target URL (TestSite runs on 7777)
const TARGET_URL = __ENV.TARGET_URL || 'http://localhost:7777';

// Helper: Random value between min and max
function randomBetween(min, max) {
    return min + Math.random() * (max - min);
}

// Helper: Build headers based on clientProfile
function buildHeaders(baseHeaders, clientProfile) {
    let headers = {};

    if (clientProfile.headerCompleteness === 'minimal') {
        // Only User-Agent
        headers = {
            'User-Agent': clientProfile.userAgent
        };
    } else if (clientProfile.headerCompleteness === 'partial') {
        // Some headers
        headers = {
            'User-Agent': clientProfile.userAgent,
            'Accept': 'text/html,application/xhtml+xml',
            'Connection': 'keep-alive'
        };
    } else if (clientProfile.headerCompleteness === 'full') {
        // Full browser headers
        headers = {
            'User-Agent': clientProfile.userAgent,
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
            'Accept-Language': 'en-US,en;q=0.9',
            'Accept-Encoding': 'gzip, deflate, br',
            'Connection': 'keep-alive',
            'Upgrade-Insecure-Requests': '1',
            'Sec-Fetch-Dest': 'document',
            'Sec-Fetch-Mode': 'navigate',
            'Sec-Fetch-Site': 'none',
            'Sec-Fetch-User': '?1'
        };

        if (clientProfile.clientHintsPresent) {
            headers['Sec-CH-UA'] = '""Chromium"";v=""120"", ""Not_A Brand"";v=""8""';
            headers['Sec-CH-UA-Mobile'] = '?0';
            headers['Sec-CH-UA-Platform'] = '""Windows""';
        }
    }

    // Merge with request-specific headers (request headers override)
    return { ...headers, ...baseHeaders };
}

");

    // Embed all signatures
    sb.AppendLine("// Embedded BDF v2 signatures");
    sb.AppendLine("const signatures = [");

    foreach (var sig in signatures)
    {
        sb.AppendLine("  {");
        sb.AppendLine($"    scenarioName: '{EscapeJs(sig.scenarioName ?? "unknown")}',");
        sb.AppendLine($"    scenario: '{EscapeJs(sig.scenario ?? "")}',");
        sb.AppendLine($"    confidence: {sig.confidence:F2},");
        sb.AppendLine($"    isBot: {(sig.confidence > 0.5 ? "true" : "false")},");

        // Client Profile
        if (sig.clientProfile != null)
        {
            sb.AppendLine("    clientProfile: {");
            sb.AppendLine($"      userAgent: '{EscapeJs(sig.clientProfile.userAgent ?? "")}',");
            sb.AppendLine($"      cookieMode: '{sig.clientProfile.cookieMode ?? "none"}',");
            sb.AppendLine($"      headerCompleteness: '{sig.clientProfile.headerCompleteness ?? "minimal"}',");
            sb.AppendLine($"      clientHintsPresent: {(sig.clientProfile.clientHintsPresent ? "true" : "false")},");
            sb.AppendLine($"      robotsConsulted: {(sig.clientProfile.robotsConsulted ? "true" : "false")}");
            sb.AppendLine("    },");
        }

        // Timing Profile
        if (sig.timingProfile != null)
        {
            sb.AppendLine("    timingProfile: {");
            sb.AppendLine($"      burstRequests: {sig.timingProfile.burstRequests},");
            if (sig.timingProfile.delayAfterMs != null)
            {
                sb.AppendLine($"      delayAfterMs: {{ min: {sig.timingProfile.delayAfterMs.min}, max: {sig.timingProfile.delayAfterMs.max} }},");
            }
            if (sig.timingProfile.pauseAfterBurstMs != null)
            {
                sb.AppendLine($"      pauseAfterBurstMs: {{ min: {sig.timingProfile.pauseAfterBurstMs.min}, max: {sig.timingProfile.pauseAfterBurstMs.max} }}");
            }
            sb.AppendLine("    },");
        }

        // Requests
        sb.AppendLine("    requests: [");
        if (sig.requests != null)
        {
            foreach (var req in sig.requests)
            {
                sb.AppendLine("      {");
                sb.AppendLine($"        method: '{req.method ?? "GET"}',");
                sb.AppendLine($"        path: '{EscapeJs(req.path ?? "/")}',");

                // Headers
                sb.Append("        headers: {");
                if (req.headers != null && req.headers.Count > 0)
                {
                    sb.Append(string.Join(", ", req.headers.Select(h => $"'{h.Key}': '{EscapeJs(h.Value)}'")));
                }
                sb.AppendLine("},");

                // Expected status codes
                if (req.expectedStatusAny != null && req.expectedStatusAny.Length > 0)
                {
                    sb.AppendLine($"        expectedStatusAny: [{string.Join(", ", req.expectedStatusAny)}],");
                }

                if (!string.IsNullOrEmpty(req.expectedOutcome))
                {
                    sb.AppendLine($"        expectedOutcome: '{req.expectedOutcome}',");
                }

                if (!string.IsNullOrEmpty(req.successCondition))
                {
                    sb.AppendLine($"        successCondition: '{EscapeJs(req.successCondition)}'");
                }

                sb.AppendLine("      },");
            }
        }
        sb.AppendLine("    ],");

        // Labels
        if (sig.labels != null && sig.labels.Length > 0)
        {
            sb.AppendLine($"    labels: [{string.Join(", ", sig.labels.Select(l => $"'{l}'"))}],");
        }

        // Evidence
        if (sig.evidence != null && sig.evidence.Length > 0)
        {
            sb.AppendLine("    evidence: [");
            foreach (var ev in sig.evidence)
            {
                sb.AppendLine($"      {{ signal: '{ev.signal}', op: '{ev.op}', value: {ev.value}, weight: {ev.weight:F2} }},");
            }
            sb.AppendLine("    ]");
        }

        sb.AppendLine("  },");
    }

    sb.AppendLine("];");
    sb.AppendLine();

    // Main test function with burst/jitter and interleaving
    sb.AppendLine(@"
// Main test function - each VU picks random scenario and replays with burst/jitter
// Multiple VUs running concurrently provide natural request interleaving
export default function() {
    const scenarioStart = Date.now();
    let lastRequestTime = Date.now();

    // Pick a random signature
    const sig = signatures[Math.floor(Math.random() * signatures.length)];

    // Track bot vs human scenarios
    if (sig.isBot) {
        botScenarios.add(1);
    } else {
        humanScenarios.add(1);
    }

    console.log(`[VU ${__VU}] Playing: ${sig.scenarioName} (confidence: ${sig.confidence})`);

    // Setup cookie jar based on cookieMode
    let jar = null;
    if (sig.clientProfile.cookieMode === 'sticky') {
        jar = http.cookieJar();
    }

    // Robots.txt consultation
    if (sig.clientProfile.robotsConsulted) {
        http.get(`${TARGET_URL}/robots.txt`);
    }

    let detectedAsBot = false;
    let requestCount = 0;

    // Replay all requests with burst/jitter timing
    for (let i = 0; i < sig.requests.length; i++) {
        const req = sig.requests[i];
        const url = `${TARGET_URL}${req.path}`;

        // Build headers based on client profile
        const headers = buildHeaders(req.headers || {}, sig.clientProfile);

        // Prepare request params
        const params = {
            headers: headers,
            tags: {
                scenario: sig.scenarioName,
                scenario_type: sig.isBot ? 'bot' : 'human',
                request_index: i,
                expected_confidence: sig.confidence
            }
        };

        // Cookie jar
        if (sig.clientProfile.cookieMode === 'none') {
            params.jar = null;
        } else if (sig.clientProfile.cookieMode === 'sticky' && jar) {
            params.jar = jar;
        }

        // Make request
        const res = http.request(req.method, url, null, params);
        totalRequests.add(1);
        requestCount++;

        // Track interval evidence
        const now = Date.now();
        intervalTrend.add(now - lastRequestTime);
        lastRequestTime = now;

        // Track sensitive path evidence
        if (req.path.includes('/admin') || req.path.includes('/api') || req.path.includes('/.')) {
            sensitivePaths.add(1);
        } else {
            sensitivePaths.add(0);
        }

        // Check response against expectedStatusAny
        if (req.expectedStatusAny && req.expectedStatusAny.length > 0) {
            check(res, {
                'status is acceptable': (r) => req.expectedStatusAny.includes(r.status)
            });
        }

        // Track bot detection
        if (res.headers['X-Bot-Detection'] === 'True' || res.status === 403) {
            detectedAsBot = true;
        }

        // Burst/jitter timing logic
        if (sig.timingProfile) {
            if (requestCount < sig.timingProfile.burstRequests) {
                // Within burst - short delay with jitter
                const jitter = randomBetween(
                    sig.timingProfile.delayAfterMs.min / 1000,
                    sig.timingProfile.delayAfterMs.max / 1000
                );
                sleep(jitter);
            } else {
                // End of burst - longer pause
                const pause = randomBetween(
                    sig.timingProfile.pauseAfterBurstMs.min / 1000,
                    sig.timingProfile.pauseAfterBurstMs.max / 1000
                );
                sleep(pause);
                requestCount = 0; // Reset burst counter
                burstRate.add(1); // Track burst pattern
            }
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
    console.log('BDF v2 Signature Replay - k6 Load Test');
    console.log('================================================================================');
    console.log(`Target URL: ${TARGET_URL}`);
    console.log(`Loaded signatures: ${signatures.length}`);
    console.log(`  - Bot scenarios: ${signatures.filter(s => s.isBot).length}`);
    console.log(`  - Human scenarios: ${signatures.filter(s => !s.isBot).length}`);
    console.log('');
    console.log('Features:');
    console.log('  ✓ Burst/jitter timing from timingProfile');
    console.log('  ✓ Cookie jar modes (none/stateless/sticky)');
    console.log('  ✓ Header completeness (minimal/partial/full)');
    console.log('  ✓ Client hints support');
    console.log('  ✓ Robots.txt consultation tracking');
    console.log('  ✓ Request interleaving via concurrent VUs');
    console.log('  ✓ Evidence signal tracking');
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
