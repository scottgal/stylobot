using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Setup;

namespace Mostlylucid.BotDetection.Test.Setup;

public class OnnxSetupResourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public OnnxSetupResourceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private IOptions<BotDetectionOptions> Opts() =>
        Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Embedding = new EmbeddingOptions { EmbeddingModel = "all-MiniLM-L6-v2.onnx" }
        });

    private string ModelsDir => Path.Combine(_tempDir, "models");
    private string ModelFile => Path.Combine(ModelsDir, "all-MiniLM-L6-v2.onnx");
    private string VocabFile => Path.Combine(ModelsDir, "vocab.txt");

    [Fact]
    public async Task CheckAsync_BothFilesMissing_ReturnsMissing()
    {
        var sut = new OnnxSetupResource(Opts());

        var result = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Missing, result.Presence);
        Assert.Equal("ONNX Embedding Model", result.Name);
        Assert.Contains("model missing", result.Detail);
        Assert.Contains("vocab missing", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_BothFilesPresent_ReturnsFresh()
    {
        Directory.CreateDirectory(ModelsDir);
        await File.WriteAllTextAsync(ModelFile, "fake-model");
        await File.WriteAllTextAsync(VocabFile, "fake-vocab");

        var sut = new OnnxSetupResource(Opts());

        var result = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Fresh, result.Presence);
        Assert.Contains("present", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_OnlyModelPresent_ReturnsMissing()
    {
        Directory.CreateDirectory(ModelsDir);
        await File.WriteAllTextAsync(ModelFile, "fake-model");

        var sut = new OnnxSetupResource(Opts());

        var result = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Missing, result.Presence);
        Assert.Contains("model ok", result.Detail);
        Assert.Contains("vocab missing", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_OnlyVocabPresent_ReturnsMissing()
    {
        Directory.CreateDirectory(ModelsDir);
        await File.WriteAllTextAsync(VocabFile, "fake-vocab");

        var sut = new OnnxSetupResource(Opts());

        var result = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Missing, result.Presence);
        Assert.Contains("model missing", result.Detail);
        Assert.Contains("vocab ok", result.Detail);
    }
}
