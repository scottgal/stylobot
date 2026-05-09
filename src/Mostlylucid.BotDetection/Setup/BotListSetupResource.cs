using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Setup;

public class BotListSetupResource : ISetupResource
{
    private readonly IBotListDatabase _database;
    private readonly string _dbPath;

    public BotListSetupResource(IBotListDatabase database, IOptions<BotDetectionOptions> options)
    {
        _database = database;
        _dbPath = options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db");
    }

    public string Name => "Bot Lists";
    public string Description => "Bot pattern lists and datacenter IP ranges (SQLite)";

    public async Task<ResourceStatus> CheckAsync(CancellationToken ct = default)
    {
        var lastUpdate = await _database.GetLastUpdateTimeAsync("bot_patterns", ct);

        if (!lastUpdate.HasValue)
            return new ResourceStatus(Name, Description, ResourcePresence.Missing, _dbPath, "Never downloaded");

        var age = DateTime.UtcNow - lastUpdate.Value;
        if (age.TotalDays > 1)
            return new ResourceStatus(Name, Description, ResourcePresence.Stale, _dbPath,
                $"Updated {(int)age.TotalDays}d ago - daily update recommended");

        return new ResourceStatus(Name, Description, ResourcePresence.Fresh, _dbPath,
            $"Updated {lastUpdate.Value:yyyy-MM-dd HH:mm} UTC");
    }

    public async Task DownloadAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        await _database.InitializeAsync(ct);
        await _database.UpdateListsAsync(ct);
        progress?.Report("Bot lists updated.");
    }
}
