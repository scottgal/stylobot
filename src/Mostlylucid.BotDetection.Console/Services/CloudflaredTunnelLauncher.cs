using System.Diagnostics;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>Result of starting a Cloudflare tunnel.</summary>
public sealed class TunnelLaunchResult
{
    public required Process Process { get; init; }
    public string? TunnelUrl { get; init; }
    public required bool IsReady { get; init; }
    public string? DiagnosticsError { get; init; }
}

public static class CloudflaredTunnelLauncher
{
    /// <summary>
    /// Start cloudflared in either quick-tunnel or named-tunnel mode.
    /// For quick tunnels (no token), sets discoveredUrl via the callback
    /// and waits up to <paramref name="urlDiscoveryTimeoutMs"/> ms.
    /// For named tunnels (token supplied), the tunnel URL is not captured
    /// from stderr - the caller provides the expected stable URL.
    /// </summary>
    public static Process? Launch(
        int port,
        string scheme,
        string? tunnelToken,
        Action<string> onTunnelUrl,
        ILogger? logger = null)
    {
        // Check cloudflared is installed
        try
        {
            var check = Process.Start(new ProcessStartInfo
            {
                FileName = "cloudflared",
                Arguments = "version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            check?.WaitForExit(5000);
            if (check is null || check.ExitCode != 0)
            {
                Log.Error("cloudflared not found. Install: brew install cloudflared (macOS) | apt install cloudflared (Linux) | winget install Cloudflare.cloudflared (Windows)");
                return null;
            }
        }
        catch
        {
            Log.Error("cloudflared not found. Install: brew install cloudflared (macOS) | apt install cloudflared (Linux) | winget install Cloudflare.cloudflared (Windows)");
            return null;
        }

        ProcessStartInfo psi;
        if (tunnelToken != null)
        {
            // Named tunnel with pre-configured token.
            // Pass the token via environment variable instead of process args
            // to avoid it appearing in process listings (ps, Task Manager, etc.).
            Log.Information("Starting Cloudflare named tunnel...");
            psi = new ProcessStartInfo
            {
                FileName = "cloudflared",
                Arguments = "tunnel run",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment["TUNNEL_TOKEN"] = tunnelToken;
        }
        else
        {
            // Quick tunnel (random *.trycloudflare.com URL)
            Log.Information("Starting Cloudflare quick tunnel...");
            psi = new ProcessStartInfo
            {
                FileName = "cloudflared",
                Arguments = $"tunnel --url {scheme}://localhost:{port}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        var process = Process.Start(psi);
        if (process == null)
        {
            Log.Error("Failed to start cloudflared process");
            return null;
        }

        // Log tunnel output in background (tunnel URL appears in stderr)
        _ = Task.Run(async () =>
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line != null)
                {
                    // Look for the tunnel URL in output
                    if (line.Contains(".trycloudflare.com") || line.Contains("Registered tunnel connection"))
                    {
                        Log.Information("  ✓ Tunnel: {TunnelInfo}", line.Trim());
                        // Extract URL from the line (e.g., "https://foo-bar.trycloudflare.com")
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"(https://[^\s|]+\.trycloudflare\.com)");
                        if (match.Success) onTunnelUrl.Invoke(match.Groups[1].Value);
                    }
                    else if (line.Contains("ERR"))
                        Log.Warning("[cloudflared] {Line}", line.Trim());
                    else
                        Log.Debug("[cloudflared] {Line}", line.Trim());
                }
            }
        });

        return process;
    }

    /// <summary>
    /// Launch cloudflared and wait for the tunnel URL to be discovered (quick tunnels)
    /// or return immediately with <c>IsReady = true</c> for named tunnels.
    /// </summary>
    /// <param name="port">Local port cloudflared should forward to.</param>
    /// <param name="scheme">http or https.</param>
    /// <param name="tunnelToken">Named tunnel token; null for a quick tunnel.</param>
    /// <param name="urlDiscoveryTimeoutMs">
    /// Maximum milliseconds to wait for the trycloudflare.com URL to appear in stderr
    /// (quick tunnels only). Defaults to 30 000 ms.
    /// </param>
    /// <param name="logger">Optional logger (falls back to Serilog static logger).</param>
    public static async Task<TunnelLaunchResult> LaunchAndWaitAsync(
        int port,
        string scheme,
        string? tunnelToken,
        int urlDiscoveryTimeoutMs = 30_000,
        ILogger? logger = null)
    {
        var urlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var process = Launch(port, scheme, tunnelToken, url => urlTcs.TrySetResult(url), logger);

        if (process == null)
        {
            // Return a dummy Process placeholder isn't possible since Process is not easily mocked,
            // but the process being null means launch failed. Callers must handle null Process.
            // We satisfy the required constraint by using a sentinel; callers should check IsReady=false.
            return new TunnelLaunchResult
            {
                Process = new Process(),
                TunnelUrl = null,
                IsReady = false,
                DiagnosticsError = "cloudflared process failed to start"
            };
        }

        // Named tunnels: URL is stable and known to the caller via the dashboard.
        // We don't parse it from stderr - return immediately as ready.
        if (tunnelToken != null)
        {
            return new TunnelLaunchResult
            {
                Process = process,
                TunnelUrl = null,
                IsReady = true
            };
        }

        // Quick tunnel: wait for the trycloudflare.com URL to appear in stderr.
        var timeoutTask = Task.Delay(urlDiscoveryTimeoutMs);
        var completed = await Task.WhenAny(urlTcs.Task, timeoutTask);

        if (completed == urlTcs.Task)
        {
            return new TunnelLaunchResult
            {
                Process = process,
                TunnelUrl = urlTcs.Task.Result,
                IsReady = true
            };
        }

        return new TunnelLaunchResult
        {
            Process = process,
            TunnelUrl = null,
            IsReady = false,
            DiagnosticsError = "Tunnel URL not discovered within timeout"
        };
    }
}
