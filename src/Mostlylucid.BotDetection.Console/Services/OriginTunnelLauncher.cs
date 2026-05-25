using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Serilog;
using SysConsole = System.Console;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Launches a <c>cloudflared access tcp</c> subprocess so stylobot can
///     reach a private origin through a Cloudflare Tunnel without that origin
///     having any public DNS / port / certificate.
/// </summary>
/// <remarks>
///     <para>
///         This is the second half of the brownfield retrofit story
///         (<c>docs/brownfield-retrofit.md</c>). The flow:
///     </para>
///     <list type="number">
///         <item>The legacy host runs <c>cloudflared tunnel run --token &lt;backend-token&gt;</c>
///               and the CF dashboard maps a private hostname to its local service.</item>
///         <item>Stylobot's host runs <c>cloudflared access tcp --hostname &lt;private-host&gt; --url localhost:&lt;port&gt;</c>
///               (this class), which opens a local TCP socket forwarding through CF.</item>
///         <item>Stylobot's upstream is rewritten to <c>http://localhost:&lt;port&gt;</c>;
///               from stylobot's point of view it's just a normal proxy hop.</item>
///     </list>
///     <para>
///         The result: neither the stylobot host nor the legacy host has any
///         inbound port open. Both speak outbound 443 to Cloudflare only.
///     </para>
/// </remarks>
public static class OriginTunnelLauncher
{
    /// <summary>
    ///     Pick a free TCP port on the loopback interface by binding to port 0
    ///     and letting the OS assign one. The socket is closed before returning
    ///     so the port is available for cloudflared to claim. Race window is
    ///     small (microseconds) -- acceptable for a startup-once primitive.
    /// </summary>
    public static int PickFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    ///     Start <c>cloudflared access tcp</c> as a sidecar that forwards a
    ///     local loopback port to a private Cloudflare Tunnel hostname.
    /// </summary>
    /// <param name="privateHostname">
    ///     The private hostname configured for the backend tunnel in the CF
    ///     dashboard (e.g. <c>oldsite.tunnel.example.org</c>).
    /// </param>
    /// <param name="localPort">
    ///     Local loopback port cloudflared should expose. Get one from
    ///     <see cref="PickFreeLoopbackPort"/>.
    /// </param>
    /// <returns>
    ///     The running cloudflared process, or <c>null</c> if cloudflared
    ///     isn't installed or failed to launch. Caller logs + degrades.
    /// </returns>
    public static Process? Launch(string privateHostname, int localPort)
    {
        if (!IsCloudflaredAvailable())
        {
            SysConsole.Error.WriteLine();
            SysConsole.Error.WriteLine("  Error: --origin-tunnel requires cloudflared. Install it first:");
            SysConsole.Error.WriteLine("    brew install cloudflared (macOS)");
            SysConsole.Error.WriteLine("    apt install cloudflared (Linux)");
            SysConsole.Error.WriteLine("    winget install Cloudflare.cloudflared (Windows)");
            SysConsole.Error.WriteLine();
            Log.Error("--origin-tunnel: cloudflared not found");
            return null;
        }

        Log.Information("Starting origin tunnel: cloudflared access tcp --hostname {Hostname} --url localhost:{Port}",
            privateHostname, localPort);

        var psi = new ProcessStartInfo
        {
            FileName = "cloudflared",
            // Hyphenated args (not equals) so cloudflared parses them correctly across versions.
            Arguments = $"access tcp --hostname {privateHostname} --url localhost:{localPort}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Process process;
        try
        {
            process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            SysConsole.Error.WriteLine($"  Error: failed to start origin-tunnel cloudflared: {ex.Message}");
            Log.Error(ex, "Failed to start origin-tunnel cloudflared process");
            return null;
        }

        var launchTime = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line == null) break;
                    if (line.Contains("ERR") || line.Contains("error") || line.Contains("fatal"))
                        Log.Warning("[origin-tunnel] {Line}", line.Trim());
                    else
                        Log.Debug("[origin-tunnel] {Line}", line.Trim());
                }

                var uptime = DateTime.UtcNow - launchTime;
                if (uptime.TotalSeconds < 5)
                {
                    SysConsole.Error.WriteLine();
                    SysConsole.Error.WriteLine(
                        $"  Warning: origin-tunnel cloudflared exited after {uptime.TotalSeconds:F1}s (exit code {process.ExitCode}).");
                    SysConsole.Error.WriteLine(
                        $"  Check that the hostname '{privateHostname}' exists in your Cloudflare Zero Trust dashboard");
                    SysConsole.Error.WriteLine(
                        "  and routes to your backend tunnel's local service. Docs:");
                    SysConsole.Error.WriteLine(
                        "    https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/private-net/cloudflared/");
                    SysConsole.Error.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[origin-tunnel] Error reading cloudflared output");
            }
        });

        return process;
    }

    /// <summary>
    ///     Lightweight check that <c>cloudflared</c> is on PATH and returns
    ///     0 from <c>cloudflared version</c>. Identical to the check the
    ///     ingress launcher does so an operator sees one cloudflared install
    ///     error per process, not two.
    /// </summary>
    private static bool IsCloudflaredAvailable()
    {
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
            return check is not null && check.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
