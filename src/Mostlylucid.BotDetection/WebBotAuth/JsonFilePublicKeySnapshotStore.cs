using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     FOSS file-backed <see cref="IPublicKeySnapshotStore"/>. Serialises the
///     fetched key snapshot to a single JSON file (key material base64-encoded).
///     A missing or corrupt file loads as null — the registry then relies on a
///     live fetch rather than the durable snapshot. Writes are atomic (temp file
///     + move) so a crash mid-write never leaves a truncated file.
/// </summary>
public sealed class JsonFilePublicKeySnapshotStore : IPublicKeySnapshotStore
{
    private readonly string _path;
    private readonly ILogger<JsonFilePublicKeySnapshotStore> _logger;

    public JsonFilePublicKeySnapshotStore(string path, ILogger<JsonFilePublicKeySnapshotStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public async Task<PublicKeySnapshot?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;

        try
        {
            await using var stream = File.OpenRead(_path);
            var file = await JsonSerializer.DeserializeAsync(
                stream, PublicKeySnapshotJsonContext.Default.PublicKeySnapshotFile, ct);
            if (file is null) return null;

            var entries = new List<PublicKeyEntry>(file.Keys.Count);
            foreach (var k in file.Keys)
            {
                if (PublicKeyManifestParser.TryToEntry(
                        k.KeyId, k.AgentName, k.PublicKey, k.Algorithm, k.NotAfter, k.Source ?? "snapshot", out var entry))
                    entries.Add(entry);
            }

            return new PublicKeySnapshot(file.SavedUtc, entries);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PublicKeyRegistry: durable snapshot at {Path} could not be read", _path);
            return null;
        }
    }

    public async Task SaveAsync(PublicKeySnapshot snapshot, CancellationToken ct = default)
    {
        var file = new PublicKeySnapshotFile
        {
            SavedUtc = snapshot.SavedUtc,
            Keys = snapshot.Keys.Select(k => new PublicKeySnapshotEntry
            {
                KeyId = k.KeyId,
                AgentName = k.AgentName,
                PublicKey = Convert.ToBase64String(k.PublicKey.Span),
                Algorithm = k.Algorithm,
                NotAfter = k.NotAfter,
                Source = k.Source
            }).ToList()
        };

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, file, PublicKeySnapshotJsonContext.Default.PublicKeySnapshotFile, ct);
        }

        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>On-disk shape of a persisted key snapshot.</summary>
public sealed class PublicKeySnapshotFile
{
    [JsonPropertyName("savedUtc")] public DateTimeOffset SavedUtc { get; set; }
    [JsonPropertyName("keys")] public List<PublicKeySnapshotEntry> Keys { get; set; } = [];
}

/// <summary>On-disk shape of one persisted key.</summary>
public sealed class PublicKeySnapshotEntry
{
    [JsonPropertyName("keyId")] public string? KeyId { get; set; }
    [JsonPropertyName("agentName")] public string? AgentName { get; set; }
    [JsonPropertyName("publicKey")] public string? PublicKey { get; set; }
    [JsonPropertyName("algorithm")] public string? Algorithm { get; set; }
    [JsonPropertyName("notAfter")] public DateTimeOffset? NotAfter { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
}

[JsonSerializable(typeof(PublicKeySnapshotFile))]
internal sealed partial class PublicKeySnapshotJsonContext : JsonSerializerContext;
