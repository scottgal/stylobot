using System.Runtime.InteropServices;

namespace Mostlylucid.BotDetection.Runtime;

/// <summary>
///     Raises the process's soft open-file-descriptor limit (<c>RLIMIT_NOFILE</c>) toward its
///     hard limit at startup, on Linux.
///     <para>
///     StyloBot is the internet-facing reverse-proxy edge: each proxied request holds a
///     downstream client socket + an upstream socket plus assorted fds (SQLite WAL handles,
///     the log file, GeoIP). A burst of concurrent connections can therefore exhaust the
///     default soft limit — 1024 on most distros — and the process dies with <c>EMFILE</c> on
///     <c>accept()</c> / <c>connect()</c>: no managed exception, not OOM, looks exactly like a
///     hard crash under load. The shipped systemd unit sets <c>LimitNOFILE=65536</c>, but a
///     bare launch (docker run, a manual foreground start, CI, the soak harness) inherits the
///     shell default. Self-raising makes the binary robust regardless of how it is started, so
///     the edge does not depend on an external unit file to survive a connection flood.
///     </para>
///     <para>
///     Raising the soft limit up to the hard limit is always permitted for an unprivileged
///     process and never lowers the limit. All failures are swallowed (fail-open) — a
///     descriptor-limit bump must never stop the gateway from booting.
///     </para>
/// </summary>
public static class FileDescriptorLimit
{
    // Linux: RLIMIT_NOFILE == 7. Deliberately Linux-only — macOS/BSD use 8, and the deploy
    // target is linux-arm64 / linux-x64. On any other platform this is a no-op.
    private const int RlimitNofile = 7;

    // getrlimit/setrlimit report an unlimited hard cap as (rlim_t)-1. Requesting an unlimited
    // soft limit can be rejected (EINVAL/EPERM against fs.nr_open) in some containers, so when
    // the hard cap is unlimited we ask for a large finite target instead.
    private const ulong RlimInfinity = ulong.MaxValue;
    private const ulong UnlimitedTarget = 1_048_576;

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Cur;
        public ulong Max;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getrlimit(int resource, ref RLimit rlim);

    [DllImport("libc", SetLastError = true)]
    private static extern int setrlimit(int resource, ref RLimit rlim);

    /// <summary>
    ///     Raises the soft fd limit toward the hard limit on Linux. Returns the (soft, hard)
    ///     descriptor limits in effect afterwards, or <c>null</c> on a non-Linux platform or if
    ///     the syscalls fail. Never throws.
    /// </summary>
    public static (ulong Soft, ulong Hard)? RaiseSoftToHard()
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            var lim = new RLimit();
            if (getrlimit(RlimitNofile, ref lim) != 0) return null;

            var target = lim.Max == RlimInfinity ? UnlimitedTarget : lim.Max;
            if (lim.Cur < target)
            {
                // Keep the hard cap unchanged (leave an unlimited hard cap unlimited); only
                // raise the soft cap. On failure keep the original soft limit and still report.
                var raised = new RLimit { Cur = target, Max = lim.Max };
                if (setrlimit(RlimitNofile, ref raised) == 0)
                    lim = raised;
            }

            return (lim.Cur, lim.Max);
        }
        catch
        {
            return null;
        }
    }
}
