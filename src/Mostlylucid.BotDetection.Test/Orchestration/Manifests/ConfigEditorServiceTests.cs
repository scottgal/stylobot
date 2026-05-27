using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration.Manifests;

/// <summary>
///     Behavioural tests for <see cref="ConfigEditorService"/>. Each test gets a fresh temp
///     root so we can assert on filesystem state without cross-test interference.
/// </summary>
public sealed class ConfigEditorServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ConfigEditorService _service;
    private readonly FileSystemConfigurationOverrideSource _source;
    private readonly DetectorManifestLoader _loader;

    public ConfigEditorServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "stylobot-config-editor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _loader = new DetectorManifestLoader();
        _loader.LoadEmbeddedManifests();
        _source = new FileSystemConfigurationOverrideSource(
            _loader,
            hostEnvironment: null,
            NullLogger<FileSystemConfigurationOverrideSource>.Instance,
            overrideRoot: _root);
        _service = new ConfigEditorService(
            _source,
            _loader,
            NullLogger<ConfigEditorService>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void ListManifests_ReturnsEmbeddedDetectors_NoneOverridden()
    {
        var list = _service.ListManifests();

        Assert.NotEmpty(list);
        Assert.Contains(list, m => m.Slug == "header");
        Assert.All(list, m => Assert.False(m.HasOverride));
    }

    [Fact]
    public void GetManifest_KnownSlug_ReturnsEmbeddedYaml()
    {
        var doc = _service.GetManifest("header");

        Assert.NotNull(doc);
        Assert.Equal("header", doc!.Slug);
        Assert.NotNull(doc.EmbeddedYaml);
        Assert.Contains("name:", doc.EmbeddedYaml, StringComparison.Ordinal);
        Assert.Null(doc.OverrideYaml);
        Assert.False(doc.HasOverride);
        // Effective falls back to embedded when no override exists.
        Assert.Equal(doc.EmbeddedYaml, doc.EffectiveYaml);
    }

    [Fact]
    public void GetManifest_UnknownSlug_ReturnsNull()
    {
        Assert.Null(_service.GetManifest("does-not-exist-anywhere"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("UPPERCASE")]
    [InlineData("has spaces")]
    [InlineData("../header")]
    [InlineData("")]
    public void GetManifest_InvalidSlug_ReturnsNull(string slug)
    {
        Assert.Null(_service.GetManifest(slug));
    }
}

