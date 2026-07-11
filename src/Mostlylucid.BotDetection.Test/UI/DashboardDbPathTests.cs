using FluentAssertions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression cover for the Demo-startup crash: a bare-filename DatabasePath (the shipped
///     Demo default, e.g. "botdetection.db") made <c>Path.GetDirectoryName</c> return ""
///     (empty, not null), so the old null-coalesce fell through to
///     <c>Directory.CreateDirectory("")</c> and threw ArgumentException before Kestrel bound.
/// </summary>
public sealed class DashboardDbPathTests
{
    [Fact]
    public void BareFilenameDatabasePath_DoesNotThrow_AndResolvesDashboardDb()
    {
        var options = new BotDetectionOptions { DatabasePath = "botdetection.db" };

        var act = () => DashboardDbPath.GetConnectionString(options);

        var connString = act.Should().NotThrow(
            "a bare filename is a documented-valid DatabasePath and must not crash startup").Which;
        connString.Should().Contain("dashboard.db");
    }

    [Fact]
    public void NullDatabasePath_DoesNotThrow()
    {
        var options = new BotDetectionOptions { DatabasePath = null };

        var act = () => DashboardDbPath.GetConnectionString(options);

        act.Should().NotThrow();
    }

    [Fact]
    public void AbsoluteDatabasePath_ResolvesUnderItsOwnDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sb-dbpath-{Guid.NewGuid():N}");
        try
        {
            var options = new BotDetectionOptions { DatabasePath = Path.Combine(dir, "botdetection.db") };

            var connString = DashboardDbPath.GetConnectionString(options);

            connString.Should().Contain(dir, "an absolute DatabasePath keeps its own directory");
            Directory.Exists(dir).Should().BeTrue("the resolved directory is created for the store");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
