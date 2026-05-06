using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Packs;

namespace Mostlylucid.BotDetection.Test.Packs;

public class PackLoaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"packs_{Guid.NewGuid():N}");

    public PackLoaderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ScanDirectory_EmptyDir_LoadsNothing()
    {
        var services = new ServiceCollection();
        var loader = new PackLoader(
            _tempDir,
            PackCapabilities.Foss,
            services,
            NullLogger<PackLoader>.Instance);

        loader.LoadAll();

        Assert.Equal(0, loader.LoadedCount);
    }

    [Fact]
    public void ScanDirectory_NonStylopackFiles_AreIgnored()
    {
        File.WriteAllText(Path.Combine(_tempDir, "readme.txt"), "not a pack");

        var services = new ServiceCollection();
        var loader = new PackLoader(
            _tempDir,
            PackCapabilities.Foss,
            services,
            NullLogger<PackLoader>.Instance);

        loader.LoadAll();

        Assert.Equal(0, loader.LoadedCount);
    }

    [Fact]
    public void ScanDirectory_YamlOnlyPack_LoadsManifest()
    {
        var packPath = Path.Combine(_tempDir, "test-pack-1.0.0.stylopack");
        using (var zip = ZipFile.Open(packPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("manifest.yaml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("""
                name: test-pack
                version: 1.0.0
                requires_tier: foss
                """);
        }

        var services = new ServiceCollection();
        var loader = new PackLoader(
            _tempDir,
            PackCapabilities.Foss,
            services,
            NullLogger<PackLoader>.Instance);

        loader.LoadAll();

        Assert.Equal(1, loader.LoadedCount);
        Assert.Equal("test-pack", loader.LoadedManifests[0].Name);
    }

    [Fact]
    public void ScanDirectory_CommercialPack_SkippedOnFoss()
    {
        var packPath = Path.Combine(_tempDir, "paid-pack-1.0.0.stylopack");
        using (var zip = ZipFile.Open(packPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("manifest.yaml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("""
                name: paid-pack
                version: 1.0.0
                requires_tier: commercial
                """);
        }

        var services = new ServiceCollection();
        var loader = new PackLoader(
            _tempDir,
            PackCapabilities.Foss,
            services,
            NullLogger<PackLoader>.Instance);

        loader.LoadAll();

        Assert.Equal(0, loader.LoadedCount);
        Assert.Single(loader.SkippedManifests);
        Assert.Equal("paid-pack", loader.SkippedManifests[0].Name);
    }
}
