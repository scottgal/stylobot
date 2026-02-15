using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     LLamaSharp-based bot name and description synthesizer (CPU-only).
///     Runs local Qwen 0.5B model via llama.cpp without external services.
///     Zero GPU overhead, pure CPU inference, ~300MB memory footprint, 50-200ms per inference.
/// </summary>
public class LlamaSharpBotNameSynthesizer : IBotNameSynthesizer, IDisposable
{
    private readonly ILogger<LlamaSharpBotNameSynthesizer> _logger;
    private readonly LlamaSharpOptions _options;
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private bool _initialized;
    private bool _initializationFailed;

    public bool IsReady => _initialized && !_initializationFailed && _model != null;

    public LlamaSharpBotNameSynthesizer(
        ILogger<LlamaSharpBotNameSynthesizer> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value.AiDetection.LlamaSharp;
    }

    /// <summary>
    ///     Initialize the model on first use (lazy loading, CPU-only).
    ///     Happens once per application lifetime.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized || _initializationFailed) return;

        try
        {
            _logger.LogInformation("Initializing LlamaSharp model (CPU-only): {ModelPath}", _options.ModelPath);

            // Build model parameters
            var modelPath = _options.ModelPath;
            var cacheDir = _options.ModelCacheDir ?? GetDefaultCacheDir();

            // If it's a HF reference, download it
            if (!File.Exists(modelPath) && !modelPath.EndsWith(".gguf"))
            {
                modelPath = await DownloadModelAsync(modelPath, cacheDir, ct);
            }

            // Verify file exists before loading
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Model file not found: {modelPath}");

            // CPU-only configuration (no GPU libraries, pure CPU backend)
            var @params = new ModelParams(modelPath)
            {
                ContextSize = (uint)_options.ContextSize
            };

            // Set thread count if specified
            if (_options.ThreadCount > 0)
                @params.Threads = _options.ThreadCount;

            _model = LLamaWeights.LoadFromFile(@params);
            _context = _model.CreateContext(@params);

            _initialized = true;
            _logger.LogInformation("LlamaSharp model initialized successfully. CPU cores: {Threads}, IsReady: {IsReady}",
                _options.ThreadCount, IsReady);
        }
        catch (Exception ex)
        {
            _initializationFailed = true;
            _logger.LogError(ex, "Failed to initialize LlamaSharp model");
        }
    }

    public async Task<string?> SynthesizeBotNameAsync(
        IReadOnlyDictionary<string, object?> signals,
        CancellationToken ct = default)
    {
        try
        {
            await EnsureInitializedAsync(ct);
            if (!IsReady) return null;

            var (name, _) = await SynthesizeDetailedAsync(signals, ct: ct);
            return name;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bot name synthesis failed");
            return null;
        }
    }

    public async Task<(string? Name, string? Description)> SynthesizeDetailedAsync(
        IReadOnlyDictionary<string, object?> signals,
        string? context = null,
        CancellationToken ct = default)
    {
        try
        {
            await EnsureInitializedAsync(ct);
            if (!IsReady) return (null, null);

            var prompt = BuildPrompt(signals, context);
            var result = await InferAsync(prompt, ct);

            if (string.IsNullOrEmpty(result)) return (null, null);

            return ParseJsonResponse(result);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bot name/description synthesis failed");
            return (null, null);
        }
    }

    private string BuildPrompt(IReadOnlyDictionary<string, object?> signals, string? context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze these bot detection signals and generate a name.");
        sb.AppendLine();

        // Extract key signals
        if (signals.TryGetValue("detection.useragent.source", out var ua))
            sb.AppendLine($"UserAgent: {ua}");
        if (signals.TryGetValue("detection.ip.type", out var ipType))
            sb.AppendLine($"IP Type: {ipType}");
        if (signals.TryGetValue("detection.behavioral.rate_limit_violations", out var rateViolations))
            sb.AppendLine($"Rate Violations: {rateViolations}");
        if (signals.TryGetValue("detection.correlation.primary_behavior", out var behavior))
            sb.AppendLine($"Behavior: {behavior}");

        if (!string.IsNullOrEmpty(context))
        {
            sb.AppendLine();
            sb.AppendLine($"Additional Context: {context}");
        }

        sb.AppendLine();
        sb.AppendLine("""
            Generate JSON response:
            {
              "name": "Bot Name (2-5 words)",
              "description": "Brief technical description (1-2 sentences)"
            }

            Respond with ONLY the JSON object, no other text.
            """);

        return sb.ToString();
    }

    private async Task<string> InferAsync(string prompt, CancellationToken ct)
    {
        if (_context == null) return string.Empty;

        try
        {
            var executor = new InteractiveExecutor(_context);
            var response = new StringBuilder();

            // Create a sampling pipeline with low temperature for reproducible classification
            var sampler = new DefaultSamplingPipeline
            {
                Temperature = _options.Temperature
            };

            var inferenceParams = new InferenceParams
            {
                MaxTokens = _options.MaxTokens,
                AntiPrompts = ["\n\n", "User:", "Assistant:"],
                SamplingPipeline = sampler
            };

            // Run inference with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));

            await foreach (var token in executor.InferAsync(prompt, inferenceParams, cts.Token))
            {
                response.Append(token);
            }

            return response.ToString();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Bot name synthesis timed out");
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Inference error");
            return string.Empty;
        }
    }

    private (string? Name, string? Description) ParseJsonResponse(string response)
    {
        try
        {
            // Extract JSON from response (model may add extra text)
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');

            if (jsonStart < 0 || jsonEnd <= jsonStart) return (null, null);

            var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var name = root.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString()?.Trim()
                : null;

            var description = root.TryGetProperty("description", out var descProp)
                ? descProp.GetString()?.Trim()
                : null;

            return (name, description);
        }
        catch
        {
            return (null, null);
        }
    }

    private string GetDefaultCacheDir()
    {
        // Priority 1: Explicit env var (for Docker volumes, custom paths)
        var envCache = Environment.GetEnvironmentVariable("STYLOBOT_MODEL_CACHE");
        if (!string.IsNullOrEmpty(envCache))
        {
            _logger.LogInformation("Using model cache from env var: {Path}", envCache);
            return envCache;
        }

        // Priority 2: Docker volume at /models (standard convention)
        var dockerVolume = "/models";
        if (Directory.Exists(dockerVolume))
        {
            _logger.LogInformation("Using Docker volume cache: {Path}", dockerVolume);
            return dockerVolume;
        }

        // Fallback: User home cache
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(homeDir, ".cache", "stylobot-models");
        _logger.LogInformation("Using local cache: {Path}", fallback);
        return fallback;
    }

    private async Task<string> DownloadModelAsync(string huggingfaceRef, string cacheDir, CancellationToken ct)
    {
        // Ensure cache directory exists
        Directory.CreateDirectory(cacheDir);

        _logger.LogInformation("Downloading model from Hugging Face: {Ref}", huggingfaceRef);

        try
        {
            // Parse HF reference (e.g., "Qwen/Qwen2.5-0.5B-Instruct-GGUF/qwen2.5-0.5b-instruct-q4_k_m.gguf")
            var parts = huggingfaceRef.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                throw new ArgumentException($"Invalid HF reference: {huggingfaceRef}");

            var owner = parts[0];
            var repo = parts[1];
            var filename = parts[^1];

            var modelPath = Path.Combine(cacheDir, $"{owner}_{repo}_{filename}");

            if (File.Exists(modelPath))
            {
                _logger.LogInformation("Model already cached at {Path}", modelPath);
                return modelPath;
            }

            // Download from HuggingFace using http client
            using var httpClient = new HttpClient();
            var hfUrl = $"https://huggingface.co/{owner}/{repo}/resolve/main/{filename}";

            _logger.LogInformation("Downloading from {Url}", hfUrl);
            var response = await httpClient.GetAsync(hfUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            using (var fileStream = File.Create(modelPath))
            {
                await contentStream.CopyToAsync(fileStream, 81920, ct);
            }

            _logger.LogInformation("Model downloaded to {Path}", modelPath);
            return modelPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model download failed. Check ModelPath configuration.");
            throw;
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _model?.Dispose();
    }
}
