using FluentAssertions;
using Mostlylucid.BotDetection.Runtime;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Runtime;

public class FileDescriptorLimitTests
{
    [Fact]
    public void RaiseSoftToHard_never_throws_and_is_platform_consistent()
    {
        var result = FileDescriptorLimit.RaiseSoftToHard();

        if (OperatingSystem.IsLinux())
        {
            // On Linux the syscalls should succeed and report limits with soft <= hard,
            // and the soft limit should be raised to (at least) the original.
            result.Should().NotBeNull();
            (result!.Value.Soft <= result.Value.Hard).Should().BeTrue();
            (result.Value.Soft > 0).Should().BeTrue();
        }
        else
        {
            // Deliberately Linux-only (the RLIMIT_NOFILE constant differs per-OS); a no-op
            // that returns null everywhere else. Must never throw.
            result.Should().BeNull();
        }
    }
}
