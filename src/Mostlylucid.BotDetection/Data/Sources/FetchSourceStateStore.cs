using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Data.Sources;

/// <summary>Persisted observation for one fetch source. Read side of <see cref="IFetchSourceStateStore"/>.</summary>
public sealed record FetchSourceObservedState(DateTimeOffset? LastSuccessUtc, DateTimeOffset? LastFailureUtc);

/// <summary>
///     Durable store for "when did this fetch source last succeed/fail" — the observed-state half of
///     the fetch registry, split from <see cref="IFetchSourceContributor"/>'s static declarations per
///     overview-'s ruling: an in-memory field answering "has this ever downloaded" resets to null on
///     every restart, which would fire the loud never-fetched alarm on every deploy. That is not a
///     hypothetical; it is the exact defect this store exists to prevent, following the same
///     precedent as the commercial <c>IThreatIntelStore</c>'s per-feed last-polled-at persistence.
/// </summary>
public interface IFetchSourceStateStore
{
    Task RecordSuccessAsync(string sourceId, DateTimeOffset atUtc, CancellationToken ct = default);
    Task RecordFailureAsync(string sourceId, DateTimeOffset atUtc, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, FetchSourceObservedState>> GetAllAsync(CancellationToken ct = default);
}

/// <summary>Where <see cref="JsonFileFetchSourceStateStore"/> persists observations. Bind from <c>BotDetection:FetchSourceState</c>.</summary>
public sealed class FetchSourceStateStoreOptions
{
    /// <summary>Default: <c>data/fetch-source-state.json</c> under <see cref="AppContext.BaseDirectory"/> if relative.</summary>
    public string FilePath { get; set; } = Path.Combine("data", "fetch-source-state.json");
}

/// <summary>
///     File-based (not database) persistence: this is deliberate, not a shortcut. Every fetch source
///     that writes durable output does so to a local file already (botdetection.db, .mmdb, etc.), so
///     a small JSON sidecar file matches the existing on-disk-artifact pattern this codebase already
///     uses, needs no new database table/migration in either repo, and works identically for FOSS
///     (no database at all) and commercial (whose DB is mid-incident as of this writing — adding a
///     new table right now is exactly the wrong moment). Atomic write (temp file + rename) so a crash
///     mid-write can't corrupt the file, matching the same pattern <c>GeoLite2UpdateService</c>
///     already uses for its own downloads.
/// </summary>
public sealed class JsonFileFetchSourceStateStore : IFetchSourceStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger<JsonFileFetchSourceStateStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonFileFetchSourceStateStore(IOptions<FetchSourceStateStoreOptions> options, ILogger<JsonFileFetchSourceStateStore>? logger = null)
    {
        var filePath = options.Value.FilePath;
        _path = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(AppContext.BaseDirectory, filePath);
        _logger = logger ?? NullLogger<JsonFileFetchSourceStateStore>.Instance;
    }

    public Task RecordSuccessAsync(string sourceId, DateTimeOffset atUtc, CancellationToken ct = default)
        => UpdateAsync(sourceId, existing => existing with { LastSuccessUtc = atUtc }, atUtc, isSuccess: true, ct);

    public Task RecordFailureAsync(string sourceId, DateTimeOffset atUtc, CancellationToken ct = default)
        => UpdateAsync(sourceId, existing => existing with { LastFailureUtc = atUtc }, atUtc, isSuccess: false, ct);

    private async Task UpdateAsync(
        string sourceId, Func<FetchSourceObservedState, FetchSourceObservedState> apply,
        DateTimeOffset atUtc, bool isSuccess, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var all = await ReadAllUnlockedAsync(ct);
            var current = all.TryGetValue(sourceId, out var existing) ? existing : new FetchSourceObservedState(null, null);
            var updated = isSuccess
                ? current with { LastSuccessUtc = atUtc }
                : current with { LastFailureUtc = atUtc };

            var next = new Dictionary<string, FetchSourceObservedState>(all) { [sourceId] = updated };
            await WriteAllUnlockedAsync(next, ct);
        }
        catch (Exception ex)
        {
            // Losing one observation write must never take down the fetcher that's reporting it -
            // the fetch itself already succeeded/failed independently of whether we can record that.
            _logger.LogWarning(ex, "Failed to persist fetch-source state for {SourceId}", sourceId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, FetchSourceObservedState>> GetAllAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            return await ReadAllUnlockedAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<Dictionary<string, FetchSourceObservedState>> ReadAllUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new Dictionary<string, FetchSourceObservedState>();

        try
        {
            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, FetchSourceObservedState>>(stream, JsonOptions, ct);
            return loaded ?? new Dictionary<string, FetchSourceObservedState>();
        }
        catch (Exception ex)
        {
            // Corrupt/partial file (e.g. a crash mid-write before atomic rename existed) must not
            // crash every reader of the registry - fail open to "no observed state", same as every
            // other fetcher's failure mode in this system.
            _logger.LogWarning(ex, "Failed to read fetch-source state file at {Path}; treating as empty", _path);
            return new Dictionary<string, FetchSourceObservedState>();
        }
    }

    private async Task WriteAllUnlockedAsync(Dictionary<string, FetchSourceObservedState> state, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, ct);
        }
        File.Move(tempPath, _path, overwrite: true);
    }
}

/// <summary>No-op store for hosts that haven't configured persistence — every source reads as
/// <see cref="FetchHealthState.Unknown"/> rather than crashing. Never the production default.</summary>
public sealed class NullFetchSourceStateStore : IFetchSourceStateStore
{
    public Task RecordSuccessAsync(string sourceId, DateTimeOffset atUtc, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordFailureAsync(string sourceId, DateTimeOffset atUtc, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, FetchSourceObservedState>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, FetchSourceObservedState>>(
            new Dictionary<string, FetchSourceObservedState>());
}
