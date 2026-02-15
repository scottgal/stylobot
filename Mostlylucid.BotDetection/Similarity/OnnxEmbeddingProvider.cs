using System.Numerics.Tensors;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     ONNX-based embedding provider using a CPU-quantized all-MiniLM-L6-v2 model.
///     Runs pure CPU inference (~1-5ms per embedding for short text).
///     Thread-safe singleton - model loaded once on first use.
///     Model and vocab are auto-downloaded from HuggingFace on first startup.
///     In Docker, the models directory should be a mapped volume for persistence.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const string ModelUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    private const string VocabUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";
    private const int MaxSequenceLength = 128;

    private readonly ILogger<OnnxEmbeddingProvider> _logger;
    private readonly string _modelPath;
    private readonly string _vocabPath;
    private readonly string _modelsDir;
    private readonly int _dimension;
    private readonly Lock _initLock = new();

    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private bool _initialized;
    private bool _available;

    public OnnxEmbeddingProvider(
        QdrantOptions qdrantOptions,
        string? databasePath,
        ILogger<OnnxEmbeddingProvider> logger)
    {
        _logger = logger;
        _dimension = qdrantOptions.EmbeddingDimension;

        _modelsDir = Path.Combine(
            databasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection-data"),
            "models");
        Directory.CreateDirectory(_modelsDir);

        _modelPath = Path.Combine(_modelsDir, qdrantOptions.EmbeddingModel);
        _vocabPath = Path.Combine(_modelsDir, "vocab.txt");
    }

    public int Dimension => _dimension;

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _available;
        }
    }

    public float[]? GenerateEmbedding(string text)
    {
        EnsureInitialized();

        if (!_available || _session == null || _tokenizer == null)
            return null;

        try
        {
            // Tokenize using v2.0 API: EncodeToIds returns IReadOnlyList<int>
            var tokenIds = _tokenizer.EncodeToIds(text, MaxSequenceLength, out _, out _);
            var seqLen = tokenIds.Count;

            // Build input tensors manually (v2.0 doesn't return attention mask directly)
            var inputIdsTensor = new DenseTensor<long>([1, seqLen]);
            var attentionMaskTensor = new DenseTensor<long>([1, seqLen]);
            var tokenTypeIdsTensor = new DenseTensor<long>([1, seqLen]);

            for (var i = 0; i < seqLen; i++)
            {
                inputIdsTensor[0, i] = tokenIds[i];
                attentionMaskTensor[0, i] = 1; // All real tokens
                tokenTypeIdsTensor[0, i] = 0;  // Single sentence
            }

            // Run ONNX inference
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
            };

            using var results = _session.Run(inputs);

            // Get last_hidden_state output: [1, seq_len, hidden_size]
            var output = results.First();
            var outputTensor = output.AsTensor<float>();
            var hiddenSize = outputTensor.Dimensions[2];

            // Mean pooling over all tokens
            var embedding = new float[hiddenSize];
            for (var i = 0; i < seqLen; i++)
            {
                for (var j = 0; j < hiddenSize; j++)
                    embedding[j] += outputTensor[0, i, j];
            }

            if (seqLen > 0)
            {
                for (var j = 0; j < hiddenSize; j++)
                    embedding[j] /= seqLen;
            }

            // L2 normalize
            var norm = MathF.Sqrt(TensorPrimitives.Dot(embedding, embedding));
            if (norm > 0)
            {
                for (var j = 0; j < embedding.Length; j++)
                    embedding[j] /= norm;
            }

            return embedding;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate ONNX embedding");
            return null;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            try
            {
                // Auto-download model + vocab if not present (Docker volume persistence)
                if (!File.Exists(_modelPath))
                {
                    _logger.LogInformation(
                        "ONNX model not found at {Path}. Auto-downloading from HuggingFace (this is a one-time download, ~90MB)...",
                        _modelPath);
                    DownloadFileAsync(ModelUrl, _modelPath).GetAwaiter().GetResult();
                }

                if (!File.Exists(_vocabPath))
                {
                    _logger.LogInformation("Vocab file not found at {Path}. Auto-downloading...", _vocabPath);
                    DownloadFileAsync(VocabUrl, _vocabPath).GetAwaiter().GetResult();
                }

                if (!File.Exists(_modelPath) || !File.Exists(_vocabPath))
                {
                    _logger.LogWarning(
                        "ONNX model or vocab not available at {Dir}. Embeddings disabled. " +
                        "To enable: place all-MiniLM-L6-v2.onnx and vocab.txt in the models directory, " +
                        "or ensure the container has internet access for auto-download.",
                        _modelsDir);
                    _available = false;
                    return;
                }

                // Load ONNX model with CPU execution
                var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = 2
                };
                _session = new InferenceSession(_modelPath, sessionOptions);

                // Load BertTokenizer from vocab.txt (v2.0 API)
                _tokenizer = BertTokenizer.Create(_vocabPath);

                _available = true;
                _logger.LogInformation(
                    "ONNX embedding provider initialized: model={Model}, dimension={Dim}, vocab={Vocab}",
                    Path.GetFileName(_modelPath), _dimension, Path.GetFileName(_vocabPath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize ONNX embedding provider. Embeddings disabled.");
                _available = false;
            }
            finally
            {
                _initialized = true;
            }
        }
    }

    private async Task DownloadFileAsync(string url, string destinationPath)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _logger.LogInformation("Downloading {Url}...", url);

            // Stream download for large files
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
            }

            _logger.LogInformation("Downloaded {File} ({Size:F1} MB)",
                Path.GetFileName(destinationPath), totalRead / 1024.0 / 1024.0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download {Url}", url);
            // Clean up partial download
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
