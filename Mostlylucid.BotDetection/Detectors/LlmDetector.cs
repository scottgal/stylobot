using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using OllamaSharp;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Advanced LLM-based bot detection with learning capabilities.
///     Uses a small language model (default: gemma3:1b) to analyze request patterns.
///     Prompt is optimized for minimal context usage (~500 tokens).
/// </summary>
public partial class LlmDetector : IDetector, IDisposable
{
    private static readonly TimeSpan ContextLengthCacheDuration = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly string _learnedPatternsPath;
    private readonly ILogger<LlmDetector> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;
    private DateTime _contextLengthFetchedAt = DateTime.MinValue;
    private bool _disposed;

    // Cached model context length (fetched once from Ollama /api/show)
    private int? _modelContextLength;

    public LlmDetector(
        ILogger<LlmDetector> logger,
        IOptions<BotDetectionOptions> options,
        IHttpClientFactory? httpClientFactory = null,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _options = options.Value;
        _metrics = metrics;
        _httpClient = httpClientFactory?.CreateClient("Ollama") ?? new HttpClient();
        _learnedPatternsPath = Path.Combine(AppContext.BaseDirectory, "learned_bot_patterns.json");
    }

    public string Name => "LLM Detector";

    /// <summary>Stage 3: AI/ML - can use all prior signals for learning</summary>
    public DetectorStage Stage => DetectorStage.Intelligence;

    public async Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new DetectorResult();

        // Skip if LLM detection is disabled (either globally or per-provider)
        if (!_options.EnableLlmDetection || !_options.AiDetection.Ollama.Enabled)
        {
            stopwatch.Stop();
            return result;
        }

        try
        {
            // Fetch context length to self-adjust request info size
            var contextLength = await GetModelContextLengthAsync(cancellationToken);
            var requestInfo = BuildRequestInfo(context, contextLength);
            var analysis = await AnalyzeWithLlm(requestInfo, cancellationToken);

            // Only report if we got a valid analysis (not the "Analysis failed" fallback)
            if (analysis.Reasoning != "Analysis failed")
            {
                if (analysis.IsBot)
                {
                    result.Confidence = analysis.Confidence;
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "LLM Analysis",
                        Detail = analysis.Reasoning,
                        ConfidenceImpact = analysis.Confidence
                    });

                    result.BotType = analysis.BotType;

                    if (analysis.Confidence > 0.8)
                        await LearnPattern(requestInfo, analysis, cancellationToken);
                }
                else
                {
                    // Human classification - add reason with negative confidence impact
                    result.Confidence = 1.0 - analysis.Confidence; // Inverse for human score
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "LLM Analysis",
                        Detail = $"LLM classified as human: {analysis.Reasoning}",
                        ConfidenceImpact = -analysis.Confidence // Negative = evidence of human
                    });
                }
            }

            stopwatch.Stop();
            _metrics?.RecordDetection(result.Confidence, result.Confidence > _options.BotThreshold, stopwatch.Elapsed,
                Name);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics?.RecordError(Name, ex.GetType().Name);
            _logger.LogWarning(ex, "LLM detection failed, continuing without it");
        }

        return result;
    }

    /// <summary>
    ///     Detect from a pre-built request info string (no HttpContext needed).
    ///     Used by the background LlmClassificationCoordinator.
    /// </summary>
    public async Task<DetectorResult> DetectFromSnapshotAsync(string preBuiltRequestInfo, CancellationToken cancellationToken = default)
    {
        var result = new DetectorResult();

        if (!_options.AiDetection.Ollama.Enabled || string.IsNullOrEmpty(_options.AiDetection.Ollama.Endpoint))
            return result;

        try
        {
            var analysis = await AnalyzeWithLlm(preBuiltRequestInfo, cancellationToken);

            if (analysis.Reasoning != "Analysis failed")
            {
                if (analysis.IsBot)
                {
                    result.Confidence = analysis.Confidence;
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "LLM Analysis",
                        Detail = analysis.Reasoning,
                        ConfidenceImpact = analysis.Confidence
                    });
                    result.BotType = analysis.BotType;
                }
                else
                {
                    result.Confidence = 1.0 - analysis.Confidence;
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "LLM Analysis",
                        Detail = $"LLM classified as human: {analysis.Reasoning}",
                        ConfidenceImpact = -analysis.Confidence
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM snapshot detection failed");
        }

        return result;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing) _fileLock.Dispose();

        _disposed = true;
    }

    /// <summary>
    ///     Fetches model context length from Ollama /api/show endpoint.
    ///     Caches result for 1 hour. Returns null if unavailable.
    /// </summary>
    private async Task<int?> GetModelContextLengthAsync(CancellationToken ct)
    {
        // Return cached value if still valid
        if (_modelContextLength.HasValue && DateTime.UtcNow - _contextLengthFetchedAt < ContextLengthCacheDuration)
            return _modelContextLength;

        try
        {
            var endpoint = _options.AiDetection.Ollama.Endpoint?.TrimEnd('/');
            var model = _options.AiDetection.Ollama.Model;

            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(model))
                return null;

            var request = new { name = model };
            var response = await _httpClient.PostAsJsonAsync($"{endpoint}/api/show", request, ct);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            // Look for num_ctx in modelfile or parameters
            // Format: "PARAMETER num_ctx 4096" or "num_ctx": 4096
            var numCtxMatch = NumCtxRegex().Match(json);
            if (numCtxMatch.Success && int.TryParse(numCtxMatch.Groups[1].Value, out var ctx))
            {
                _modelContextLength = ctx;
                _contextLengthFetchedAt = DateTime.UtcNow;
                _logger.LogDebug("Fetched model context length: {ContextLength} for {Model}", ctx, model);
                return ctx;
            }

            // Default: gemma3:4b has 8192 context, most small models have 2048-4096
            _modelContextLength = 2048;
            _contextLengthFetchedAt = DateTime.UtcNow;
            return _modelContextLength;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch model context length, using default");
            return null;
        }
    }

    /// <summary>
    ///     Builds ultra-compact request info for LLM analysis.
    ///     Self-adjusts based on model context length.
    ///     Focus: Heuristic prob + highest-confidence signals only.
    ///     Format: TOML-like, no whitespace waste.
    /// </summary>
    private string BuildRequestInfo(HttpContext context, int? contextLength = null)
    {
        // Adjust limits based on context length
        // ~4 chars per token, reserve 200 tokens for prompt + 100 for response
        var maxChars = contextLength.HasValue
            ? Math.Max(200, (contextLength.Value - 300) * 4)
            : 800; // Conservative default

        var uaMaxLen = contextLength switch
        {
            >= 8192 => 150,
            >= 4096 => 100,
            _ => 60
        };
        var topDetectors = contextLength switch
        {
            >= 8192 => 5,
            >= 4096 => 3,
            _ => 2
        };

        var sb = new StringBuilder();
        var evidence = context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] as AggregatedEvidence;

        // Heuristic probability is the key signal - put it first
        if (evidence != null) sb.Append($"prob={evidence.BotProbability:F2}\n");

        // Core request data - ultra compact
        var ua = context.Request.Headers.UserAgent.ToString();
        sb.Append($"ua=\"{(ua.Length > uaMaxLen ? ua[..(uaMaxLen - 3)] + "..." : ua)}\"\n");

        var lang = context.Request.Headers.AcceptLanguage.ToString();
        var referer = context.Request.Headers.Referer.ToString();
        var hasCookies = context.Request.Cookies.Any();
        var hdrs = context.Request.Headers.Count;

        // Compact: only include if meaningful
        if (!string.IsNullOrEmpty(lang))
        {
            var langMax = contextLength >= 4096 ? 30 : 15;
            sb.Append($"lang=\"{(lang.Length > langMax ? lang[..(langMax - 3)] + "..." : lang)}\"\n");
        }

        if (!string.IsNullOrEmpty(referer)) sb.Append("ref=1\n");
        if (hasCookies) sb.Append("cookies=1\n");
        sb.Append($"hdrs={hdrs}\n");

        if (evidence == null || sb.Length > maxChars)
            return sb.ToString();

        // Check if localhost
        var isLocalhost = evidence.Signals.TryGetValue(SignalKeys.IpIsLocal, out var isLocal) && isLocal is true;

        // Only show highest-confidence detectors
        var topHits = evidence.Contributions
            .Where(c => !isLocalhost || !c.DetectorName.Equals("Ip", StringComparison.OrdinalIgnoreCase))
            .Where(c => Math.Abs(c.ConfidenceDelta) >= 0.1) // Only significant signals
            .OrderByDescending(c => Math.Abs(c.ConfidenceDelta) * c.Weight)
            .Take(topDetectors)
            .ToList();

        if (topHits.Count != 0 && sb.Length < maxChars - 100)
        {
            sb.Append("[top]\n");
            var reasonMax = contextLength >= 4096 ? 20 : 12;
            foreach (var c in topHits)
            {
                if (sb.Length > maxChars - 50) break;

                var name = c.DetectorName switch
                {
                    "Heuristic" => "H",
                    "UserAgent" => "UA",
                    "Header" => "Hdr",
                    "Ip" => "IP",
                    "Behavioral" => "Beh",
                    "SecurityTool" => "Sec",
                    _ => c.DetectorName.Length > 3 ? c.DetectorName[..3] : c.DetectorName
                };
                var reason = c.Reason.Length > reasonMax ? c.Reason[..(reasonMax - 3)] + "..." : c.Reason;
                sb.Append($"{name}={c.ConfidenceDelta:+0.0;-0.0}|\"{reason}\"\n");
            }
        }

        // Bot type hint if detected
        if (evidence.PrimaryBotType.HasValue && evidence.PrimaryBotType != BotType.Unknown && sb.Length < maxChars - 20)
            sb.Append($"type={evidence.PrimaryBotType}\n");

        return sb.ToString();
    }

    private async Task<LlmAnalysis> AnalyzeWithLlm(string requestInfo, CancellationToken cancellationToken)
    {
        // Use new AiDetection settings (legacy properties are deprecated)
        var timeout = _options.AiDetection.TimeoutMs;
        var endpoint = _options.AiDetection.Ollama.Endpoint;
        var model = _options.AiDetection.Ollama.Model;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var ollama = new OllamaApiClient(endpoint!)
            {
                SelectedModel = model
            };

            // Use custom prompt if configured, otherwise use default compact prompt
            var promptTemplate = !string.IsNullOrEmpty(_options.AiDetection.Ollama.CustomPrompt)
                ? _options.AiDetection.Ollama.CustomPrompt
                : OllamaOptions.DefaultPrompt;

            // Replace placeholder with actual request info
            var prompt = promptTemplate.Replace("{REQUEST_INFO}", requestInfo);

            var chat = new Chat(ollama);
            var responseBuilder = new StringBuilder();

            await foreach (var token in chat.SendAsync(prompt, cts.Token))
                responseBuilder.Append(token);

            var response = responseBuilder.ToString();

            // Check if response is empty (Ollama may have failed silently)
            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning(
                    "LLM returned empty response. Ollama may have failed to generate output for model '{Model}'",
                    model);
                return new LlmAnalysis { IsBot = false, Confidence = 0.0, Reasoning = "Analysis failed" };
            }

            // Check for Ollama error responses
            if (response.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                (response.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                 response.Contains("failed", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogError("Ollama returned an error: {Response}",
                    response.Length > 500 ? response[..500] + "..." : response);
                return new LlmAnalysis { IsBot = false, Confidence = 0.0, Reasoning = "Analysis failed" };
            }

            // Strip markdown code fences if present (```json ... ``` or ``` ... ```)
            var cleanedResponse = StripMarkdownCodeFences(response);

            // JSON options for case-insensitive parsing (LLM outputs camelCase, C# uses PascalCase)
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            // Try to parse as JSON first
            try
            {
                var analysisResult = JsonSerializer.Deserialize<LlmAnalysisJson>(cleanedResponse, jsonOptions);
                if (analysisResult != null)
                {
                    _logger.LogDebug(
                        "LLM response parsed: isBot={IsBot}, confidence={Confidence}, reasoning={Reasoning}",
                        analysisResult.IsBot, analysisResult.Confidence, analysisResult.Reasoning);
                    return CreateAnalysis(analysisResult);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Direct JSON parse failed, trying to extract JSON from response");
            }

            // Extract JSON from response (model might include extra text)
            var jsonStart = cleanedResponse.IndexOf('{');
            var jsonEnd = cleanedResponse.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonText = cleanedResponse.Substring(jsonStart, jsonEnd - jsonStart + 1);
                try
                {
                    var analysisResult = JsonSerializer.Deserialize<LlmAnalysisJson>(jsonText, jsonOptions);
                    if (analysisResult != null)
                    {
                        _logger.LogDebug(
                            "LLM response extracted and parsed: isBot={IsBot}, confidence={Confidence}, reasoning={Reasoning}",
                            analysisResult.IsBot, analysisResult.Confidence, analysisResult.Reasoning);
                        return CreateAnalysis(analysisResult);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Failed to parse extracted JSON: {Json}", jsonText);
                }
            }

            // Try to extract partial values from truncated JSON (common with timeouts)
            var partialResult = TryExtractPartialJson(cleanedResponse);
            if (partialResult != null)
            {
                _logger.LogDebug(
                    "LLM response partially parsed from truncated JSON: isBot={IsBot}, confidence={Confidence}",
                    partialResult.IsBot, partialResult.Confidence);
                return partialResult;
            }

            _logger.LogWarning(
                "LLM returned invalid JSON (model '{Model}' may need a better prompt). Response: {Response}", model,
                response.Length > 500 ? response[..500] + "..." : response);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "LLM analysis timed out after {Timeout}ms. Consider increasing AiDetection.TimeoutMs (current: {Timeout}ms)",
                timeout, timeout);
        }
        catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogError("Ollama model '{Model}' not found at {Endpoint}. Run 'ollama pull {Model}' to download it",
                model, endpoint, model);
        }
        catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.InternalServerError)
        {
            _logger.LogError(
                "Ollama server error (500) at {Endpoint}. Check Ollama logs - the model '{Model}' may have failed to load or run out of memory",
                endpoint, model);
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "Ollama HTTP error ({StatusCode}) at {Endpoint}. Is Ollama running?",
                (int?)httpEx.StatusCode ?? 0, endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM analysis failed: {Message}", ex.Message);
        }

        return new LlmAnalysis { IsBot = false, Confidence = 0.0, Reasoning = "Analysis failed" };
    }

    private LlmAnalysis CreateAnalysis(LlmAnalysisJson result)
    {
        return new LlmAnalysis
        {
            IsBot = result.IsBot,
            Confidence = Math.Clamp(result.Confidence, 0.0, 1.0),
            Reasoning = result.Reasoning ?? "No reasoning provided",
            BotType = ParseBotType(result.BotType),
            Pattern = result.Pattern
        };
    }

    /// <summary>
    ///     Try to extract partial values from truncated JSON responses.
    ///     This handles cases where the LLM response was cut off (timeout, token limit).
    /// </summary>
    private static LlmAnalysis? TryExtractPartialJson(string text)
    {
        // Look for isBot value with regex
        var isBotMatch = IsBotRegex().Match(text);

        if (!isBotMatch.Success)
            return null;

        bool isBot;
        if (isBotMatch.Groups[2].Success)
            // Numeric value: 0 = false, anything else = true
            isBot = isBotMatch.Groups[2].Value != "0";
        else
            isBot = isBotMatch.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

        // Try to extract confidence
        var confidenceMatch = ConfidenceRegex().Match(text);

        var confidence = isBot ? 0.7 : 0.3; // Default if not found
        if (confidenceMatch.Success && double.TryParse(confidenceMatch.Groups[1].Value, out var parsedConf))
            confidence = parsedConf > 1 ? parsedConf / 100.0 : parsedConf; // Handle percentage vs decimal

        // Try to extract reasoning
        var reasoningMatch = ReasoningRegex().Match(text);

        var reasoning = reasoningMatch.Success
            ? reasoningMatch.Groups[1].Value + " (partial response)"
            : "Partial response from LLM";

        return new LlmAnalysis
        {
            IsBot = isBot,
            Confidence = confidence,
            Reasoning = reasoning,
            BotType = BotType.Unknown
        };
    }

    private async Task LearnPattern(string requestInfo, LlmAnalysis analysis, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(analysis.Pattern))
            return;

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var learnedPatterns = new List<LearnedPattern>();

            if (File.Exists(_learnedPatternsPath))
            {
                var json = await File.ReadAllTextAsync(_learnedPatternsPath, cancellationToken);
                learnedPatterns = JsonSerializer.Deserialize<List<LearnedPattern>>(json) ?? new List<LearnedPattern>();
            }

            var newPattern = new LearnedPattern
            {
                Pattern = analysis.Pattern,
                BotType = analysis.BotType.ToString(),
                Confidence = analysis.Confidence,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                OccurrenceCount = 1,
                ExampleRequest = requestInfo
            };

            var existing = learnedPatterns.FirstOrDefault(p =>
                p.Pattern.Equals(analysis.Pattern, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.LastSeen = DateTime.UtcNow;
                existing.OccurrenceCount++;
                existing.Confidence = Math.Max(existing.Confidence, analysis.Confidence);
            }
            else
            {
                learnedPatterns.Add(newPattern);
                _logger.LogInformation("Learned new bot pattern: {Pattern}", analysis.Pattern);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = JsonSerializer.Serialize(learnedPatterns, options);
            await File.WriteAllTextAsync(_learnedPatternsPath, updatedJson, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save learned pattern");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Strips markdown code fences from LLM response.
    ///     Handles: ```json ... ```, ``` ... ```, and variations.
    /// </summary>
    private static string StripMarkdownCodeFences(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        var trimmed = response.Trim();

        // Handle ```json or ```JSON or ``` at the start
        if (trimmed.StartsWith("```"))
        {
            // Find the end of the first line (after ```json or ```)
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
                trimmed = trimmed[(firstNewline + 1)..];
            else
                // Just ``` on one line, skip it
                trimmed = trimmed[3..];
        }

        // Handle ``` at the end
        if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];

        return trimmed.Trim();
    }

    private static BotType ParseBotType(string? botType)
    {
        if (string.IsNullOrEmpty(botType))
            return BotType.Unknown;

        return botType.ToLowerInvariant() switch
        {
            "scraper" => BotType.Scraper,
            "searchengine" => BotType.SearchEngine,
            "monitor" or "monitoring" => BotType.MonitoringBot,
            "malicious" => BotType.MaliciousBot,
            "social" or "socialmedia" => BotType.SocialMediaBot,
            "good" or "verified" => BotType.GoodBot,
            _ => BotType.Unknown
        };
    }

    private class LlmAnalysis
    {
        public bool IsBot { get; set; }
        public double Confidence { get; set; }
        public required string Reasoning { get; set; }
        public BotType BotType { get; set; }
        public string? Pattern { get; set; }
    }

    private class LlmAnalysisJson
    {
        public bool IsBot { get; set; }
        public double Confidence { get; set; }
        public string? Reasoning { get; set; }
        public string? BotType { get; set; }
        public string? Pattern { get; set; }
    }

    [GeneratedRegex(@"num_ctx["":\s]+(\d+)")]
    private static partial Regex NumCtxRegex();

    [GeneratedRegex(@"""?is_?bot""?\s*:\s*(true|false|""?(\d+)""?)", RegexOptions.IgnoreCase)]
    private static partial Regex IsBotRegex();

    [GeneratedRegex(@"""?confidence""?\s*:\s*""?(\d+\.?\d*)""?", RegexOptions.IgnoreCase)]
    private static partial Regex ConfidenceRegex();

    [GeneratedRegex(@"""?reasoning""?\s*:\s*""([^""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningRegex();
}

/// <summary>
///     Learned bot pattern stored in JSON
/// </summary>
public class LearnedPattern
{
    public required string Pattern { get; set; }
    public required string BotType { get; set; }
    public double Confidence { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public int OccurrenceCount { get; set; }
    public string? ExampleRequest { get; set; }
}