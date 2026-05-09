using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Setup;
using Mostlylucid.GeoDetection.Contributor.Setup;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Test.Setup;

public class GeoIpSetupResourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();

    public GeoIpSetupResourceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // DatabasePath ends with .csv and is already rooted, so ComputeCsvPath returns it as-is.
    private string CsvPath => Path.Combine(_tempDir, "data", "GeoLite2-City.csv");

    private IOptions<GeoLite2Options> Opts() => Options.Create(new GeoLite2Options
    {
        Provider = GeoProvider.DataHubCsv,
        DatabasePath = Path.Combine(_tempDir, "data", "GeoLite2-City.csv")
    });

    [Fact]
    public async Task CheckAsync_WhenCsvMissing_ReturnsMissing()
    {
        var sut = new GeoIpSetupResource(Opts(), _httpClientFactoryMock.Object);

        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Missing, status.Presence);
    }

    [Fact]
    public async Task CheckAsync_WhenCsvFresh_ReturnsFresh()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CsvPath)!);
        await File.WriteAllTextAsync(CsvPath, "fake csv data");
        File.SetLastWriteTimeUtc(CsvPath, DateTime.UtcNow.AddHours(-1));

        var sut = new GeoIpSetupResource(Opts(), _httpClientFactoryMock.Object);
        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Fresh, status.Presence);
    }

    [Fact]
    public async Task CheckAsync_WhenCsvOlderThan7Days_ReturnsStale()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CsvPath)!);
        await File.WriteAllTextAsync(CsvPath, "fake csv data");
        File.SetLastWriteTimeUtc(CsvPath, DateTime.UtcNow.AddDays(-10));

        var sut = new GeoIpSetupResource(Opts(), _httpClientFactoryMock.Object);
        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Stale, status.Presence);
    }
}
