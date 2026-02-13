using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Starts the Demo app as a child process on a random port for Puppeteer integration tests.
///     Avoids WebApplicationFactory assembly-loading issues with the test host.
/// </summary>
public sealed class DemoAppFactory : IAsyncLifetime
{
    private static readonly string DemoProjectDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Mostlylucid.BotDetection.Demo"));

    private readonly int _port = GetAvailablePort();
    private Process? _process;

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "run --no-build --no-launch-profile",
                WorkingDirectory = DemoProjectDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{_port}",
                    ["ASPNETCORE_ENVIRONMENT"] = "Development"
                }
            }
        };
        _process.Start();

        // Wait for the server to become responsive (may take time for GeoIP download on first run)
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new InvalidOperationException(
                    $"Demo app exited with code {_process.ExitCode} before becoming responsive.");

            try
            {
                var response = await client.GetAsync($"{BaseUrl}/api");
                if ((int)response.StatusCode < 500)
                    return; // Any non-5xx response means the server is up
            }
            catch (Exception)
            {
                // Server not ready yet
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Demo app did not start within 60 seconds on {BaseUrl}");
    }

    public Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }

        _process?.Dispose();
        return Task.CompletedTask;
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
///     xUnit collection definition to share the Demo app server across all Puppeteer test classes.
///     All test classes using [Collection("DemoApp")] share a single server instance.
/// </summary>
[CollectionDefinition("DemoApp")]
public class DemoAppCollection : ICollectionFixture<DemoAppFactory>;
