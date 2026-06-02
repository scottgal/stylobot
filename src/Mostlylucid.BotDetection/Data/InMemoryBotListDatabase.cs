using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Helpers;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     In-memory bot list database for economy / ephemeral mode. Fetches patterns
///     and CIDR ranges via <see cref="IBotListFetcher"/> on first init and holds
///     them in <see cref="HashSet{T}"/> / <see cref="List{T}"/>. No SQLite, no
///     <c>botdetection.db</c> on disk -- true zero-persistence.
///
///     <para>
///     Trade-offs vs the SQLite-backed default:
///     <list type="bullet">
///       <item>Pattern lookup is a linear scan with regex match; same as the
///             SQLite path (which also iterates rows in <see cref="GetBotInfo"/>).
///             ~20K patterns at request rate is acceptable on the slow path.</item>
///       <item>Updates re-fetch + replace the in-memory snapshot. No persistence.</item>
///       <item>Restart re-fetches every list -- one-shot HTTP burst on cold start.</item>
///     </list>
///     </para>
///
///     Wired in via <c>AddBotDetectionInMemory()</c>.
/// </summary>
public sealed class InMemoryBotListDatabase : IBotListDatabase, IDisposable
{
    private readonly IBotListFetcher _fetcher;
    private readonly ILogger<InMemoryBotListDatabase> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Dictionary<string, DateTime> _lastUpdate = new(StringComparer.OrdinalIgnoreCase);

    // Snapshots are swapped atomically via volatile assignment; readers see a
    // consistent view without locking. Initial empty snapshot means IsBot etc.
    // return false until InitializeAsync completes.
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private bool _initialized;

    public InMemoryBotListDatabase(IBotListFetcher fetcher, ILogger<InMemoryBotListDatabase> logger)
    {
        _fetcher = fetcher;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            _logger.LogInformation("Initializing in-memory bot list database (economy mode -- no SQLite)");
            await RefreshSnapshotAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> IsBot(string userAgent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return false;
        await InitializeAsync(cancellationToken);
        var snap = _snapshot;
        foreach (var rx in snap.BotRegexes)
        {
            if (rx.IsMatch(userAgent)) return true;
        }
        return false;
    }

    public async Task<BotInfo?> GetBotInfo(string userAgent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return null;
        await InitializeAsync(cancellationToken);
        var snap = _snapshot;
        // Matomo entries (with metadata) checked first; fall back to plain pattern hit.
        foreach (var entry in snap.BotEntriesWithMetadata)
        {
            if (entry.Regex.IsMatch(userAgent))
                return new BotInfo
                {
                    Name = entry.Name,
                    Category = entry.Category,
                    Url = entry.Url,
                    IsVerified = false,
                };
        }
        foreach (var rx in snap.BotRegexes)
        {
            if (rx.IsMatch(userAgent))
                return new BotInfo { Name = "Bot", Category = "generic", IsVerified = false };
        }
        return null;
    }

    public async Task<bool> IsDatacenterIp(string ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) return false;
        await InitializeAsync(cancellationToken);
        var snap = _snapshot;
        foreach (var range in snap.DatacenterRanges)
        {
            if (CidrHelper.IsInSubnet(ipAddress, range)) return true;
        }
        return false;
    }

    public async Task UpdateListsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        _logger.LogInformation("Refreshing in-memory bot lists (economy mode)");
        await RefreshSnapshotAsync(cancellationToken);
    }

    public Task<DateTime?> GetLastUpdateTimeAsync(string listType, CancellationToken cancellationToken = default)
        => Task.FromResult(_lastUpdate.TryGetValue(listType, out var dt) ? (DateTime?)dt : null);

    public Task<IReadOnlyList<string>> GetBotPatternsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(_snapshot.BotPatterns);

    public Task<IReadOnlyList<string>> GetDatacenterIpRangesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(_snapshot.DatacenterRanges);

    private async Task RefreshSnapshotAsync(CancellationToken ct)
    {
        var botPatterns = new List<string>();
        var matomo = new List<BotEntry>();
        var ipRanges = new List<string>();

        try
        {
            botPatterns = await _fetcher.GetBotPatternsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "in-memory bot list: GetBotPatternsAsync failed -- proceeding with empty set");
        }

        try
        {
            var matomoRaw = await _fetcher.GetMatomoBotPatternsAsync(ct);
            foreach (var m in matomoRaw)
            {
                if (string.IsNullOrWhiteSpace(m.Pattern)) continue;
                var rx = TryCompileRegex(m.Pattern);
                if (rx is null) continue;
                matomo.Add(new BotEntry(rx, m.Name ?? "Bot", m.Category ?? "matomo", m.Url));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "in-memory bot list: GetMatomoBotPatternsAsync failed -- skipping metadata layer");
        }

        try
        {
            ipRanges = await _fetcher.GetDatacenterIpRangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "in-memory bot list: GetDatacenterIpRangesAsync failed -- proceeding with empty set");
        }

        var compiledBotRegexes = new List<Regex>(botPatterns.Count);
        foreach (var p in botPatterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var rx = TryCompileRegex(p);
            if (rx is not null) compiledBotRegexes.Add(rx);
        }

        var now = DateTime.UtcNow;
        _lastUpdate["bot_patterns"] = now;
        _lastUpdate["matomo"] = now;
        _lastUpdate["datacenter_ips"] = now;

        _snapshot = new Snapshot(
            BotPatterns: botPatterns,
            BotRegexes: compiledBotRegexes,
            BotEntriesWithMetadata: matomo,
            DatacenterRanges: ipRanges);

        _logger.LogInformation(
            "in-memory bot list snapshot refreshed: {BotPatterns} bot patterns, {Matomo} matomo entries, {Ranges} datacenter ranges",
            compiledBotRegexes.Count, matomo.Count, ipRanges.Count);
    }

    private static Regex? TryCompileRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Dispose() => _initLock.Dispose();

    private sealed record Snapshot(
        IReadOnlyList<string> BotPatterns,
        IReadOnlyList<Regex> BotRegexes,
        IReadOnlyList<BotEntry> BotEntriesWithMetadata,
        IReadOnlyList<string> DatacenterRanges)
    {
        public static readonly Snapshot Empty = new(
            Array.Empty<string>(), Array.Empty<Regex>(),
            Array.Empty<BotEntry>(), Array.Empty<string>());
    }

    private sealed record BotEntry(Regex Regex, string Name, string Category, string? Url);
}
