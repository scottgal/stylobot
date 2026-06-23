using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Starts the Demo app as a child process on a random port for browser-driven
///     (Playwright) integration tests and the BDF replay harness. Avoids
///     WebApplicationFactory assembly-loading issues with the test host.
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
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    // Quieten the BotListUpdateService periodic loop in tests.
                    // The first request still lazy-inits the BotListDatabase
                    // (and may pull from useragents.me / coreruleset) but
                    // there's no second refresh while the test suite runs --
                    // gives consistent behaviour across runs.
                    ["BotDetection__EnableBackgroundUpdates"] = "false",
                    // Disable the aggressive demo calibration trigger (1s min,
                    // observation threshold = 1) so calibration cannot mutate
                    // archetype variance multipliers / centroids mid-scenario.
                    // The BDF rig is asserting deterministic matcher output
                    // against frozen YAML archetypes; learning during a replay
                    // makes "fp-chrome-windows-human matched to ublock-origin"
                    // (umbrella-centroid drift) appear intermittently as
                    // calibration ticks happen to fire before vs. after the
                    // archetype set is read by the matcher. Falls back to the
                    // wall-clock CalibrationIntervalMinutes = 1, which exceeds
                    // a full BDF replay scenario's duration.
                    ["BotDetection__Identity__Calibration__Trigger__Enabled"] = "false",
                },
            },
        };
        _process.Start();
        // Capture Demo stdout/stderr to a known temp file so test-time
        // debugging can grep for archetype allocations etc.
        var logPath = $"/tmp/demo-app-{_port}.log";
        _ = Task.Run(async () =>
        {
            using var f = new StreamWriter(logPath, append: false) { AutoFlush = true };
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync()) != null)
                await f.WriteLineAsync(line);
        });
        _ = Task.Run(async () =>
        {
            using var f = new StreamWriter(logPath + ".err", append: false) { AutoFlush = true };
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync()) != null)
                await f.WriteLineAsync(line);
        });
        Console.WriteLine($"[DemoAppFactory] demo logs -> {logPath} (+.err)");

        // Two-phase wait. First phase: get any non-5xx response from /api --
        // confirms Kestrel is listening and routes are mapped. Second phase:
        // pre-warm /api by issuing a successful request and waiting for it to
        // complete; the FIRST /api hit triggers BotListDatabase lazy init,
        // which pulls bot patterns + datacenter IP ranges from external CDNs
        // and can take 5-15s. Doing it here (inside the fixture init) keeps
        // the actual test methods' GotoAsync(30s default) calls fast.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var deadline = DateTime.UtcNow.AddSeconds(120);
        var sawAlive = false;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new InvalidOperationException(
                    $"Demo app exited with code {_process.ExitCode} before becoming responsive.");

            try
            {
                var response = await client.GetAsync($"{BaseUrl}/api");
                if ((int)response.StatusCode < 500)
                {
                    if (!sawAlive)
                    {
                        // First success -- read the body to force the response
                        // to fully materialize so the bot-list lazy init runs
                        // to completion before we hand back to the test.
                        await response.Content.ReadAsStringAsync();
                        sawAlive = true;
                        // One more pre-warm pass to make sure subsequent
                        // requests find populated caches.
                        await client.GetStringAsync($"{BaseUrl}/api");
                    }
                    return;
                }
            }
            catch (Exception)
            {
                // Server not ready yet
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Demo app did not start within 120 seconds on {BaseUrl}");
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
///     xUnit collection definition to share the Demo app server across all test classes
///     that use it. <c>DisableParallelization = true</c> is essential: BDF replay
///     scenarios share one Demo instance and reset the identity store between scenarios.
///     Without serialisation, theory cases run concurrently against the same process,
///     one scenario's <c>/reset-identity</c> races with another scenario's
///     <c>/replay</c> requests, and identity emission becomes flaky (1/N requests miss
///     <c>identity.fingerprint_id</c> as the matcher's per-call store reads see a
///     transiently-empty fingerprints.db mid-reset).
/// </summary>
[CollectionDefinition("DemoApp", DisableParallelization = true)]
public class DemoAppCollection : ICollectionFixture<DemoAppFactory>;
