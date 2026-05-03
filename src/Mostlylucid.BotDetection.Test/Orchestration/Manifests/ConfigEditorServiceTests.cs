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

    [Fact]
    public void SaveOverride_ValidYaml_WritesAtomicallyAndReportsHasOverride()
    {
        const string yaml = "name: HeaderContributor\npriority: 99\nenabled: true\n";

        var result = _service.SaveOverride("header", yaml);

        Assert.Equal(SaveOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Path);
        Assert.True(File.Exists(result.Path));
        Assert.Equal(yaml, File.ReadAllText(result.Path));

        // Subsequent GetManifest reflects the override.
        var doc = _service.GetManifest("header");
        Assert.NotNull(doc);
        Assert.True(doc!.HasOverride);
        Assert.Equal(yaml, doc.OverrideYaml);
        Assert.Equal(yaml, doc.EffectiveYaml);
    }

    [Fact]
    public void SaveOverride_InvalidYaml_DoesNotWriteFile_AndReportsLineColumn()
    {
        // Bad indentation that the YAML parser will reject.
        const string yaml = "name: header\npriority: bogus\n  also-bad-indent\n";

        var result = _service.SaveOverride("header", yaml);

        Assert.Equal(SaveOutcome.YamlInvalid, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "detectors", "header.detector.yaml")));
    }

    [Fact]
    public void SaveOverride_PreservesPreviousFile_WhenNewYamlInvalid()
    {
        // Seed a known-good override.
        var firstSave = _service.SaveOverride("header", "name: HeaderContributor\npriority: 11\n");
        Assert.Equal(SaveOutcome.Ok, firstSave.Outcome);
        var path = firstSave.Path!;

        // Try to save something that won't parse - the original file must survive.
        var badSave = _service.SaveOverride("header", "name: HeaderContributor\npriority: bogus\n  weird-indent");
        Assert.Equal(SaveOutcome.YamlInvalid, badSave.Outcome);
        Assert.True(File.Exists(path));
        Assert.Contains("priority: 11", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("UPPERCASE")]
    public void SaveOverride_InvalidSlug_Refused(string slug)
    {
        var result = _service.SaveOverride(slug, "name: x\n");
        Assert.Equal(SaveOutcome.InvalidSlug, result.Outcome);
    }

    [Fact]
    public void SaveOverride_UnknownDetector_Refused()
    {
        var result = _service.SaveOverride("totally-fake-detector", "name: x\n");
        Assert.Equal(SaveOutcome.UnknownDetector, result.Outcome);
    }

    [Fact]
    public void SaveOverride_OversizedBody_Refused()
    {
        var huge = new string('x', 300_000);
        var result = _service.SaveOverride("header", huge);
        Assert.Equal(SaveOutcome.TooLarge, result.Outcome);
    }

    [Fact]
    public void DeleteOverride_RemovesFile_AndIsIdempotent()
    {
        _service.SaveOverride("header", "name: HeaderContributor\npriority: 11\n");
        var path = Path.Combine(_root, "detectors", "header.detector.yaml");
        Assert.True(File.Exists(path));

        var first = _service.DeleteOverride("header");
        Assert.Equal(DeleteOutcome.Ok, first.Outcome());
        Assert.False(File.Exists(path));

        // Second delete returns NotFound, doesn't throw.
        var second = _service.DeleteOverride("header");
        Assert.Equal(DeleteOutcome.NotFound, second.Outcome());
    }

    [Fact]
    public void ListManifests_ReflectsOverridesAfterSave()
    {
        Assert.False(_service.ListManifests().Single(m => m.Slug == "header").HasOverride);

        _service.SaveOverride("header", "name: HeaderContributor\npriority: 11\n");

        Assert.True(_service.ListManifests().Single(m => m.Slug == "header").HasOverride);
    }
}

internal static class DeleteOutcomeExtensions
{
    // The enum is bare on purpose - wrapping in a struct/record adds noise. This shim
    // lets tests read like `result.Outcome()` for symmetry with SaveResult.
    public static DeleteOutcome Outcome(this DeleteOutcome o) => o;
}