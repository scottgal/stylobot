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

        // The daemon must survive when the launching shell (SSH session, console,
        // etc.) exits. Inheriting the parent's stdout/stderr is fatal in that case
        // because the OS closes those handles when the launcher process detaches —
        // the daemon then crashes on its first log write to a dead FD.
        //
        // Detach properly per platform:
        //   Windows: launch through `cmd.exe /c start "" /B` which forks a new
        //            process group, then redirect output to daemon-startup.log.
        //   Unix:    use the existing nohup-style pipe (read+discard prevents the
        //            pipe-full blocking, and the parent dies cleanly because we
        //            do not BeginRead it).
        Process? process;
        var logDir = DefaultLogDir;
        Directory.CreateDirectory(logDir);
        var bootLog = Path.Combine(logDir, "daemon-startup.log");

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Two problems to solve on Windows when called from a console
                // (cmd.exe, SSH session, terminal) that will exit immediately:
                //
                // 1) Handle inheritance. The naive `RedirectStandardOutput = false`
                //    path inherits the parent's stdout/stderr. When the launcher
                //    shell exits, those handles become invalid and the gateway
                //    crashes on its first log write.
                //
                // 2) Console group sharing. All descendants of a console-attached
                //    process share the same console. When the launcher shell exits
                //    (SSH disconnect, terminal close), Windows sends
                //    CTRL_CLOSE_EVENT to every process attached to that console,
                //    triggering the gateway's graceful shutdown handler. The
                //    gateway dies even though its file handles are fine.
                //
                // Fix: use `start "" /B cmd /c "exe args >> log 2>&1"`. The outer
                // `start /B` creates a new process group (detaches from the
                // console), the inner cmd handles the file redirection that fixes
                // problem (1). Wrapped inside a top-level cmd because start is a
                // cmd builtin, not an executable.
                var inner = $"\"{exePath}\" {string.Join(' ', childArgs.Select(EscapeArg))} >> \"{bootLog}\" 2>&1";
                var outer = $"/c start \"\" /B cmd /c {inner}";
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = outer,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                process = Process.Start(psi);

                // The Process we got back is the outer cmd.exe; resolve the actual
                // gateway PID by looking for the most recently-started
                // stylobot.exe so PID-file consumers (status, stop) target the
                // right process.
                if (process != null)
                {
                    var gateway = FindRecentStylobotProcessWithRetry(exePath,
                        since: DateTime.Now.AddSeconds(-3),
                        maxWaitMs: 5000);
                    if (gateway != null) process = gateway;
                }
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = string.Join(' ', childArgs.Select(EscapeArg)),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                process = Process.Start(psi);
            }
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
        if (IsSymlink(PidFile) || IsSymlink(PortFile))
        {
            System.Console.Error.WriteLine($"  Refusing to write through a symlinked PID/port file in {PidDir}");
            try { process.Kill(true); } catch { /* child may have already exited */ }
            return 1;
        }
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

        if (IsSymlink(PidFile))
        {
            System.Console.Error.WriteLine($"  Refusing to read symlinked PID file: {PidFile}");
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

        // PID recycling guard: never signal a process that isn't ours. Same
        // identity rule as TryGetRunningProcess.
        var processName = SafeProcessName(process);
        if (!processName.Contains("stylobot") && !processName.Contains("dotnet"))
        {
            System.Console.Error.WriteLine($"  PID {pid} is not a stylobot process ('{processName}') - removing stale PID file");
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

        if (IsSymlink(PidFile))
        {
            System.Console.Error.WriteLine($"  Refusing to read symlinked PID file: {PidFile}");
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
        if (IsSymlink(PidFile)) return false;

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

    /// <summary>
    ///     The PID/port files live in the invoking user's home dir, but a planted
    ///     symlink would let "stylobot start" overwrite an arbitrary file and
    ///     "stylobot stop" signal whatever PID the link target names. Refuse both.
    /// </summary>
    private static bool IsSymlink(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists && fi.LinkTarget != null;
    }

    private static string SafeProcessName(Process process)
    {
        try { return process.ProcessName.ToLowerInvariant(); }
        catch { return string.Empty; }
    }

    /// <summary>
    ///     After spawning the daemon through cmd.exe on Windows, look up the
    ///     actual stylobot.exe child by matching process name + recent start
    ///     time. Returns the youngest stylobot process whose StartTime is at
    ///     or after <paramref name="since"/>.
    /// </summary>
    private static Process? FindRecentStylobotProcess(string exePath, DateTime since)
    {
        var exeName = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrEmpty(exeName)) exeName = "stylobot";

        Process? best = null;
        foreach (var p in Process.GetProcessesByName(exeName))
        {
            try
            {
                if (p.HasExited) continue;
                if (p.StartTime < since) continue;
                if (best == null || p.StartTime > best.StartTime) best = p;
            }
            catch
            {
                // Process exited or access denied; ignore
            }
        }
        return best;
    }

    /// <summary>
    ///     Polls FindRecentStylobotProcess until a fresh process appears or
    ///     <paramref name="maxWaitMs"/> elapses. Handles the spawn-race: cmd.exe
    ///     may take 50-500ms to fork the gateway, and the parent stylobot
    ///     (which IS in the process list) must be excluded.
    /// </summary>
    private static Process? FindRecentStylobotProcessWithRetry(string exePath, DateTime since, int maxWaitMs)
    {
        var deadline = DateTime.Now.AddMilliseconds(maxWaitMs);
        var ourPid = Process.GetCurrentProcess().Id;
        var exeName = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrEmpty(exeName)) exeName = "stylobot";

        while (DateTime.Now < deadline)
        {
            Process? best = null;
            foreach (var p in Process.GetProcessesByName(exeName))
            {
                try
                {
                    if (p.Id == ourPid) continue;     // exclude self
                    if (p.HasExited) continue;
                    if (p.StartTime < since) continue;
                    if (best == null || p.StartTime > best.StartTime) best = p;
                }
                catch { /* exited or access denied */ }
            }
            if (best != null) return best;
            System.Threading.Thread.Sleep(100);
        }
        return null;
    }

    private static string EscapeArg(string arg)
    {
        if (!arg.Contains(' ') && !arg.Contains('"') && !arg.Contains('\\'))
            return arg;
        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
