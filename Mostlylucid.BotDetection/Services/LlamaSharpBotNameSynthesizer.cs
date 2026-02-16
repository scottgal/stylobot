using System.Collections.Concurrent;
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
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private volatile bool _initialized;
    private volatile bool _initializationFailed;

    // Track recently used names for uniqueness (ring buffer of last 200 names)
    private readonly ConcurrentQueue<string> _usedNames = new();
    private const int MaxUsedNamesTracked = 200;

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

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized || _initializationFailed) return;
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
        finally
        {
            _initLock.Release();
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

            var (name, description) = ParseJsonResponse(result);

            // Track the name for uniqueness in future prompts
            if (!string.IsNullOrEmpty(name))
                TrackUsedName(name);

            return (name, description);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bot name/description synthesis failed");
            return (null, null);
        }
    }

    /// <summary>
    ///     Track a name that was generated so future prompts can avoid duplicates.
    /// </summary>
    private void TrackUsedName(string name)
    {
        _usedNames.Enqueue(name);
        while (_usedNames.Count > MaxUsedNamesTracked)
            _usedNames.TryDequeue(out _);
    }

    /// <summary>
    ///     Get recently used names to pass to the LLM for uniqueness enforcement.
    ///     Limits count to avoid blowing the context window on small models.
    /// </summary>
    private List<string> GetRecentlyUsedNames(int maxNames = 20)
    {
        return _usedNames.ToArray().TakeLast(maxNames).ToList();
    }

    private string BuildPrompt(IReadOnlyDictionary<string, object?> signals, string? context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a creative bot naming expert. Analyze these detection signals and generate a unique, descriptive, and slightly humorous bot name.");
        sb.AppendLine("The name should reflect the bot's behavior/origin in a witty way (like 'Captain Crawlspace', 'The Headless Harvester', 'Señor Scrape-a-Lot', 'ByteNinja 3000').");
        sb.AppendLine();

        // Budget: ~600 tokens for signals to stay well within context window.
        // Qwen 0.5B default context is 2048 tokens; prompt preamble + JSON instruction ~300 tokens,
        // used names ~100 tokens, leaving ~600 for signals and ~1000 for generation.
        const int maxSignalChars = 1200; // rough ~600 token budget (2 chars/token avg)
        var signalBudget = maxSignalChars;

        void TryAddSignal(string label, object? value)
        {
            if (value == null || signalBudget <= 0) return;
            var line = $"{label}: {value}";
            // Truncate very long values (e.g. full user agent strings)
            if (line.Length > 200) line = line[..200] + "...";
            if (line.Length <= signalBudget)
            {
                sb.AppendLine(line);
                signalBudget -= line.Length;
            }
        }

        // Extract key signals — most informative first
        signals.TryGetValue("ua.bot_type", out var botType);
        TryAddSignal("Bot Type", botType);
        signals.TryGetValue("ua.bot_name", out var knownName);
        TryAddSignal("Known Bot", knownName);
        signals.TryGetValue("detection.useragent.source", out var ua);
        TryAddSignal("UserAgent", ua);
        signals.TryGetValue("detection.ip.type", out var ipType);
        TryAddSignal("IP Type", ipType);
        signals.TryGetValue("ip.is_datacenter", out var dc);
        TryAddSignal("Datacenter", dc);
        signals.TryGetValue("geo.country_code", out var country);
        TryAddSignal("Country", country);
        signals.TryGetValue("detection.behavioral.rate_limit_violations", out var rateViolations);
        TryAddSignal("Rate Violations", rateViolations);
        signals.TryGetValue("detection.correlation.primary_behavior", out var behavior);
        TryAddSignal("Behavior", behavior);
        signals.TryGetValue("detection.heuristic.probability", out var heurProb);
        TryAddSignal("Bot Probability", heurProb);
        signals.TryGetValue("tls.ja3_hash", out var ja3);
        TryAddSignal("TLS Fingerprint", ja3);
        signals.TryGetValue("waveform.traversal_pattern", out var traversal);
        TryAddSignal("Traversal", traversal);

        // Include up to 5 signature vectors for diversity (within budget)
        var sigCount = 0;
        foreach (var (key, value) in signals)
        {
            if (sigCount >= 5) break;
            if (key.StartsWith("signature.") && value != null)
            {
                TryAddSignal(key, value);
                sigCount++;
            }
        }

        if (!string.IsNullOrEmpty(context) && signalBudget > 50)
        {
            sb.AppendLine();
            var ctxLine = $"Additional Context: {context}";
            if (ctxLine.Length > 200) ctxLine = ctxLine[..200] + "...";
            sb.AppendLine(ctxLine);
        }

        // Add previously used names to avoid duplicates (limit to 20 names max)
        var usedNames = GetRecentlyUsedNames(20);
        if (usedNames.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Do NOT reuse these names: {string.Join(", ", usedNames)}");
        }

        sb.AppendLine();
        sb.AppendLine("""
            Respond with ONLY this JSON:
            {
              "name": "Funny Bot Name (2-5 words)",
              "description": "What this bot does (1 sentence)"
            }
            """);

        return sb.ToString();
    }

    private async Task<string> InferAsync(string prompt, CancellationToken ct)
    {
        if (_context == null) return string.Empty;

        // Serialize inference — LLamaContext is not thread-safe
        await _inferenceLock.WaitAsync(ct);
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
        finally
        {
            _inferenceLock.Release();
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

            // Download to temp file first, then rename atomically to avoid corrupted partial downloads
            var tempPath = modelPath + ".tmp";
            try
            {
                using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
                using (var fileStream = File.Create(tempPath))
                {
                    await contentStream.CopyToAsync(fileStream, 81920, ct);
                }

                File.Move(tempPath, modelPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
                throw;
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
        _initLock.Dispose();
        _inferenceLock.Dispose();
    }
}
