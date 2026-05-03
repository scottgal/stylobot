using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Daemon lifecycle commands: start (background), stop, status, logs.
///     PID file tracks the running instance.
/// </summary>
public static class DaemonCommands
{
    private static string PidDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "stylobot");

    private static string PidFile => Path.Combine(PidDir, "stylobot.pid");
    private static string PortFile => Path.Combine(PidDir, "stylobot.port");

    private static string DefaultLogDir => Path.Combine(
        Mostlylucid.BotDetection.Models.BotDetectionOptions.ResolveDataDirectory(), "logs");

    /// <summary>
    ///     Start stylobot as a background process. Writes PID file.
    ///     Re-launches the same binary with the original args minus "start".
    /// </summary>
    public static int Start(string[] originalArgs)
    {
        // Check if already running
        if (TryGetRunningProcess(out var existingPid))
        {
            System.Console.Error.WriteLine($"  stylobot is already running (PID {existingPid})");
            System.Console.Error.WriteLine($"  Use 'stylobot stop' to stop it first");
            return 1;
        }

        // Build args for the child process: remove "start" subcommand, add --verbose
        // (background process should log to file, not render Spectre table)
        var childArgs = new List<string>();
        for (var i = 1; i < originalArgs.Length; i++)
        {
            if (originalArgs[i].Equals("start", StringComparison.OrdinalIgnoreCase)) continue;
            childArgs.Add(originalArgs[i]);
        }
        if (!childArgs.Contains("--verbose"))
            childArgs.Add("--verbose");

        // Find our own binary path
        var exePath = Environment.ProcessPath ?? originalArgs[0];

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = string.Join(' ', childArgs.Select(EscapeArg)),
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };

        // On Unix, redirect stdout/stderr to log files for background process
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var logDir = DefaultLogDir;
            Directory.CreateDirectory(logDir);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"  Failed to start: {ex.Message}");
            return 1;
        }

        if (process == null)
        {
            System.Console.Error.WriteLine("  Failed to start background process");
            return 1;
        }

        // Write PID file + port file
        Directory.CreateDirectory(PidDir);
        File.WriteAllText(PidFile, process.Id.ToString());

        // Extract port from child args for status command
        var portArg = childArgs.FirstOrDefault(a => int.TryParse(a, out _));
        if (portArg != null)
            File.WriteAllText(PortFile, portArg);

        System.Console.WriteLine();
        System.Console.WriteLine($"  stylobot started (PID {process.Id})");
        System.Console.WriteLine($"  PID file: {PidFile}");
        System.Console.WriteLine($"  Logs:     {DefaultLogDir}");
        System.Console.WriteLine();
        System.Console.WriteLine($"  Stop:     stylobot stop");
        System.Console.WriteLine($"  Status:   stylobot status");
        System.Console.WriteLine($"  Logs:     stylobot logs");
        System.Console.WriteLine();

        return 0;
    }

    /// <summary>
    ///     Stop a running stylobot daemon by sending SIGTERM / killing the process.
    /// </summary>
    public static int Stop()
    {
        if (!File.Exists(PidFile))
        {
            System.Console.Error.WriteLine("  stylobot is not running (no PID file)");
            return 1;
        }

        var pidText = File.ReadAllText(PidFile).Trim();
        if (!int.TryParse(pidText, out var pid))
        {
            System.Console.Error.WriteLine($"  Invalid PID file: {PidFile}");
            File.Delete(PidFile);
            return 1;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            System.Console.Error.WriteLine($"  Process {pid} is not running (stale PID file)");
            File.Delete(PidFile);
            return 1;
        }

        System.Console.WriteLine($"  Stopping stylobot (PID {pid})...");

        try
        {
            // Graceful shutdown: SIGTERM on Unix, Kill on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                process.Kill();
            else
                process.Kill(false); // SIGTERM via Kill(false) on .NET 8+

            // Wait up to 10 seconds for graceful shutdown
            if (!process.WaitForExit(10_000))
            {
                System.Console.WriteLine("  Forcing kill...");
                process.Kill(true);
                process.WaitForExit(5_000);
            }
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"  Failed to stop: {ex.Message}");
            return 1;
        }
        finally
        {
            File.Delete(PidFile);
            if (File.Exists(PortFile)) File.Delete(PortFile);
        }

        System.Console.WriteLine("  stylobot stopped");
        return 0;
    }

    /// <summary>
    ///     Check if stylobot is running and show status info.
    /// </summary>
    public static async Task<int> Status()
    {
        if (!File.Exists(PidFile))
        {
            System.Console.WriteLine("  stylobot is not running");
            return 1;
        }

        var pidText = File.ReadAllText(PidFile).Trim();
        if (!int.TryParse(pidText, out var pid))
        {
            System.Console.WriteLine("  Invalid PID file (removing)");
            File.Delete(PidFile);
            return 1;
        }

        // Check process exists
        bool alive;
        try
        {
            var process = Process.GetProcessById(pid);
            alive = !process.HasExited;
        }
        catch (ArgumentException)
        {
            alive = false;
        }

        if (!alive)
        {
            System.Console.WriteLine($"  stylobot is not running (stale PID {pid}, cleaning up)");
            File.Delete(PidFile);
            return 1;
        }

        System.Console.WriteLine($"  stylobot is running (PID {pid})");

        // Try to hit health endpoint - check saved port first, then common ports
        var ports = new List<string>();
        if (File.Exists(PortFile))
            ports.Add(File.ReadAllText(PortFile).Trim());
        ports.AddRange(new[] { "5080", "8080", "443", "80" });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        foreach (var p in ports.Distinct())
        {
            try
            {
                var response = await http.GetStringAsync($"http://localhost:{p}/health");
                System.Console.WriteLine($"  Health:   http://localhost:{p}/health");
                System.Console.WriteLine($"  Response: {response}");
                return 0;
            }
            catch
            {
                // Try next port
            }
        }

        System.Console.WriteLine("  Health endpoint not reachable (check port)");
        return 0;
    }

    /// <summary>
    ///     Tail the most recent log file.
    /// </summary>
    public static int Logs(int lines = 50)
    {
        var logDir = DefaultLogDir;
        if (!Directory.Exists(logDir))
        {
            System.Console.Error.WriteLine($"  No log directory found at {logDir}");
            return 1;
        }

        var logFiles = Directory.GetFiles(logDir, "errors-*.log")
            .OrderByDescending(f => f)
            .ToList();

        if (logFiles.Count == 0)
        {
            System.Console.Error.WriteLine($"  No log files found in {logDir}");
            return 1;
        }

        var latestLog = logFiles[0];
        System.Console.WriteLine($"  Log file: {latestLog}");
        System.Console.WriteLine(new string('-', 60));

        // Read last N lines
        var allLines = File.ReadAllLines(latestLog);
        var startLine = Math.Max(0, allLines.Length - lines);
        for (var i = startLine; i < allLines.Length; i++)
            System.Console.WriteLine(allLines[i]);

        return 0;
    }

    /// <summary>
    ///     Check if a stylobot process is running from the PID file.
    /// </summary>
    private static bool TryGetRunningProcess(out int pid)
    {
        pid = 0;
        if (!File.Exists(PidFile)) return false;

        var pidText = File.ReadAllText(PidFile).Trim();
        if (!int.TryParse(pidText, out pid)) return false;

        try
        {
            var process = Process.GetProcessById(pid);
            if (process.HasExited) { File.Delete(PidFile); return false; }

            // Verify it's actually a stylobot process (not a recycled PID)
            var name = process.ProcessName.ToLowerInvariant();
            if (!name.Contains("stylobot") && !name.Contains("dotnet"))
            {
                File.Delete(PidFile);
                return false;
            }
            return true;
        }
        catch (ArgumentException)
        {
            // Process not found - stale PID
            File.Delete(PidFile);
            return false;
        }
    }

    private static string EscapeArg(string arg)
    {
        if (!arg.Contains(' ') && !arg.Contains('"') && !arg.Contains('\\'))
            return arg;
        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
