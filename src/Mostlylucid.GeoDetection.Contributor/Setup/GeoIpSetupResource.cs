using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Setup;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Contributor.Setup;

public class GeoIpSetupResource : ISetupResource
{
    private const string CsvUrl = "https://datahub.io/core/geoip2-ipv4/r/geoip2-ipv4.csv";
    private const string DefaultCsvPath = "data/geoip2-ipv4.csv";
    private readonly string _csvPath;
    private readonly IHttpClientFactory _httpClientFactory;

    public GeoIpSetupResource(IOptions<GeoLite2Options> options, IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _csvPath = ComputeCsvPath(options.Value.DatabasePath);
    }

    /// <summary>
    ///     Mirrors DataHubGeoLocationService.GetCsvPath() exactly.
    ///     - If DatabasePath ends with .mmdb, change extension to .csv.
    ///     - If it still doesn't end with .csv, fall back to DefaultCsvPath.
    ///     - If not rooted, combine with AppContext.BaseDirectory.
    /// </summary>
    private static string ComputeCsvPath(string? databasePath)
    {
        var path = databasePath ?? DefaultCsvPath;

        if (path.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, ".csv");

        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            path = DefaultCsvPath;

        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppContext.BaseDirectory, path);

        return path;
    }

    public string Name => "GeoIP CSV Database";
    public string Description => "DataHub GeoIP2-IPv4 country database (~27MB), updated weekly";

    public Task<ResourceStatus> CheckAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_csvPath))
            return Task.FromResult(new ResourceStatus(Name, Description, ResourcePresence.Missing, _csvPath,
                "File not found"));

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_csvPath);
        if (age.TotalDays > 7)
            return Task.FromResult(new ResourceStatus(Name, Description, ResourcePresence.Stale, _csvPath,
                $"Updated {(int)age.TotalDays} days ago -- weekly update recommended"));

        return Task.FromResult(new ResourceStatus(Name, Description, ResourcePresence.Fresh, _csvPath,
            $"Updated {(int)age.TotalHours}h ago"));
    }

    public async Task DownloadAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        progress?.Report("Downloading GeoIP CSV database (~27MB)...");
        var dir = Path.GetDirectoryName(_csvPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var client = _httpClientFactory.CreateClient("DataHub");
        using var response = await client.GetAsync(CsvUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(_csvPath);
        await content.CopyToAsync(file, ct);

        progress?.Report($"GeoIP database saved to {_csvPath}");
    }
}
