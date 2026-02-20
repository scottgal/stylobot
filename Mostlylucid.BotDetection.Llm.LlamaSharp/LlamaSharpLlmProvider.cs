using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Llm.LlamaSharp;

/// <summary>
///     ILlmProvider backed by LLamaSharp (in-process CPU inference via llama.cpp).
///     Lazy model initialization, SemaphoreSlim-serialized inference, HuggingFace auto-download.
///     Extracted from LlamaSharpBotNameSynthesizer.
/// </summary>
public class LlamaSharpLlmProvider : ILlmProvider, IDisposable
{
    private readonly ILogger<LlamaSharpLlmProvider> _logger;
    private readonly LlamaSharpProviderOptions _options;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private volatile bool _initialized;
    private volatile bool _initializationFailed;

    public LlamaSharpLlmProvider(
        ILogger<LlamaSharpLlmProvider> logger,
        IOptions<LlamaSharpProviderOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public bool IsReady => _initialized && !_initializationFailed && _model != null;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized || _initializationFailed) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized || _initializationFailed) return;
            _logger.LogInformation("Initializing LlamaSharp model (CPU-only): {ModelPath}", _options.ModelPath);

            var modelPath = _options.ModelPath;
            var cacheDir = _options.ModelCacheDir ?? GetDefaultCacheDir();

            if (!File.Exists(modelPath) && !modelPath.EndsWith(".gguf"))
            {
                modelPath = await DownloadModelAsync(modelPath, cacheDir, ct);
            }

            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Model file not found: {modelPath}");

            var @params = new ModelParams(modelPath)
            {
                ContextSize = (uint)_options.ContextSize
            };

            if (_options.ThreadCount > 0)
                @params.Threads = _options.ThreadCount;

            _model = LLamaWeights.LoadFromFile(@params);
            _context = _model.CreateContext(@params);

            _initialized = true;
            _logger.LogInformation("LlamaSharp model initialized. CPU cores: {Threads}, IsReady: {IsReady}",
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

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (_context == null) return string.Empty;

        await _inferenceLock.WaitAsync(ct);
        try
        {
            var executor = new InteractiveExecutor(_context);
            var response = new StringBuilder();

            var sampler = new DefaultSamplingPipeline
            {
                Temperature = request.Temperature
            };

            var inferenceParams = new InferenceParams
            {
                MaxTokens = request.MaxTokens,
                AntiPrompts = ["\n\n", "User:", "Assistant:"],
                SamplingPipeline = sampler
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(request.TimeoutMs));

            await foreach (var token in executor.InferAsync(request.Prompt, inferenceParams, cts.Token))
            {
                response.Append(token);
            }

            return response.ToString();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("LlamaSharp inference timed out");
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LlamaSharp inference error");
            return string.Empty;
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private string GetDefaultCacheDir()
    {
        var envCache = Environment.GetEnvironmentVariable("STYLOBOT_MODEL_CACHE");
        if (!string.IsNullOrEmpty(envCache))
            return envCache;

        var dockerVolume = "/models";
        if (Directory.Exists(dockerVolume))
            return dockerVolume;

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".cache", "stylobot-models");
    }

    private async Task<string> DownloadModelAsync(string huggingfaceRef, string cacheDir, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDir);
        _logger.LogInformation("Downloading model from Hugging Face: {Ref}", huggingfaceRef);

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

        using var httpClient = new HttpClient();
        var hfUrl = $"https://huggingface.co/{owner}/{repo}/resolve/main/{filename}";

        _logger.LogInformation("Downloading from {Url}", hfUrl);
        var response = await httpClient.GetAsync(hfUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

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

    public void Dispose()
    {
        _context?.Dispose();
        _model?.Dispose();
        _initLock.Dispose();
        _inferenceLock.Dispose();
    }
}
