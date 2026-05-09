using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Setup;

public class OnnxSetupResource : ISetupResource
{
    private const string ModelUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";

    private const string VocabUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    private readonly string _modelPath;
    private readonly string _modelsDir;
    private readonly string _vocabPath;

    public OnnxSetupResource(IOptions<BotDetectionOptions> options)
    {
        var opts = options.Value;
        var baseDir = opts.DatabasePath != null
            ? Path.GetDirectoryName(opts.DatabasePath) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;
        _modelsDir = Path.Combine(baseDir, "models");
        _modelPath = Path.Combine(_modelsDir, opts.Qdrant.EmbeddingModel);
        _vocabPath = Path.Combine(_modelsDir, "vocab.txt");
    }

    public string Name => "ONNX Embedding Model";
    public string Description => "all-MiniLM-L6-v2 semantic similarity model (~90MB)";

    public Task<ResourceStatus> CheckAsync(CancellationToken ct = default)
    {
        var modelExists = File.Exists(_modelPath);
        var vocabExists = File.Exists(_vocabPath);

        if (modelExists && vocabExists)
            return Task.FromResult(new ResourceStatus(Name, Description, ResourcePresence.Fresh, _modelsDir,
                "Model and vocab present"));

        var detail =
            $"{(modelExists ? "model ok" : "model missing")}, {(vocabExists ? "vocab ok" : "vocab missing")}";
        return Task.FromResult(new ResourceStatus(Name, Description, ResourcePresence.Missing, _modelsDir, detail));
    }

    public async Task DownloadAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_modelsDir);

        if (!File.Exists(_modelPath))
        {
            progress?.Report("Downloading ONNX model (~90MB) from HuggingFace...");
            await DownloadFileAsync(ModelUrl, _modelPath, ct);
            progress?.Report($"  Model saved to {_modelPath}");
        }

        if (!File.Exists(_vocabPath))
        {
            progress?.Report("Downloading vocab.txt...");
            await DownloadFileAsync(VocabUrl, _vocabPath, ct);
        }
    }

    private static async Task DownloadFileAsync(string url, string dest, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
        await stream.CopyToAsync(file, ct);
    }
}
