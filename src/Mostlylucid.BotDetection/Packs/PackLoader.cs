using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.BotDetection.Packs;

public sealed class PackLoader(
    string packsDirectory,
    IPackCapabilities capabilities,
    IServiceCollection services,
    ILogger<PackLoader> logger)
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly List<PackManifest> _loaded = [];
    private readonly List<PackManifest> _skipped = [];

    public int LoadedCount => _loaded.Count;
    public IReadOnlyList<PackManifest> LoadedManifests => _loaded;
    public IReadOnlyList<PackManifest> SkippedManifests => _skipped;

    public void LoadAll()
    {
        if (!Directory.Exists(packsDirectory))
            return;

        foreach (var path in Directory.EnumerateFiles(packsDirectory, "*.stylopack"))
            LoadOne(path);
    }

    private void LoadOne(string path)
    {
        PackManifest? manifest = null;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifestEntry = zip.GetEntry("manifest.yaml");
            if (manifestEntry == null)
            {
                logger.LogWarning("Pack {Path} has no manifest.yaml, skipping", path);
                return;
            }

            using var reader = new StreamReader(manifestEntry.Open());
            manifest = Deserializer.Deserialize<PackManifest>(reader.ReadToEnd());

            if (string.IsNullOrWhiteSpace(manifest.Name))
            {
                logger.LogWarning("Pack {Path} manifest has no name, skipping", path);
                return;
            }

            var requiredTier = manifest.RequiresTier;
            if (requiredTier.Equals("commercial", StringComparison.OrdinalIgnoreCase) && !capabilities.CanWrite)
            {
                logger.LogInformation(
                    "Pack {Name} requires commercial tier, skipping on {Tier}",
                    manifest.Name, capabilities.Tier);
                _skipped.Add(manifest);
                return;
            }

            if (!string.IsNullOrWhiteSpace(manifest.EntryType) && !string.IsNullOrWhiteSpace(manifest.Assembly))
                LoadAssemblyPack(zip, manifest);

            _loaded.Add(manifest);
            logger.LogInformation("Loaded pack {Name} v{Version}", manifest.Name, manifest.Version);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load pack {Path}", path);
            if (manifest != null) _skipped.Add(manifest);
        }
    }

    private void LoadAssemblyPack(ZipArchive zip, PackManifest manifest)
    {
        var dllEntry = zip.GetEntry(manifest.Assembly!);
        if (dllEntry == null)
        {
            logger.LogWarning("Pack {Name} declares assembly {Assembly} but it is not in the zip",
                manifest.Name, manifest.Assembly);
            return;
        }

        var tempDll = Path.Combine(Path.GetTempPath(), $"{manifest.Name}_{Guid.NewGuid():N}.dll");
        try
        {
            using (var fs = File.Create(tempDll))
            using (var entryStream = dllEntry.Open())
                entryStream.CopyTo(fs);

            var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(tempDll);
            var entryType = asm.GetType(manifest.EntryType!);
            if (entryType == null || !typeof(IStylobotPack).IsAssignableFrom(entryType))
            {
                logger.LogWarning("Pack {Name} entry type {Type} not found or does not implement IStylobotPack",
                    manifest.Name, manifest.EntryType);
                return;
            }

            var pack = (IStylobotPack)Activator.CreateInstance(entryType)!;
            pack.ConfigureServices(services, capabilities);
            logger.LogInformation("Pack {Name} configured services", manifest.Name);
        }
        finally
        {
            try { File.Delete(tempDll); } catch { /* best effort */ }
        }
    }
}
