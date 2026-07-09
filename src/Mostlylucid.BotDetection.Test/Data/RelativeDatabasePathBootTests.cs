using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Regression guard for the boot-breaker deploy- hit on main 2d3231e0: a bare /
///     relative <c>DatabasePath</c> (for example <c>"botdetection.db"</c>, which is
///     exactly what the Demo/Sample/TrailblazorDemo appsettings set after #49) has
///     <c>Path.GetDirectoryName</c> == <c>""</c> (empty, not null), so the stores'
///     <c>GetDirectoryName(x) ?? AppContext.BaseDirectory</c> fallback missed it and
///     <c>Directory.CreateDirectory("")</c> threw ArgumentException at construction,
///     failing the host closed at boot. The centroid stores construct eagerly at boot,
///     so this is the first thing that crashes.
/// </summary>
public class RelativeDatabasePathBootTests
{
    private static IOptions<BotDetectionOptions> RelativePath() =>
        Options.Create(new BotDetectionOptions { DatabasePath = "botdetection.db" });

    [Fact]
    public void SignatureCentroidStore_constructs_with_a_relative_databasepath()
    {
        var ex = Record.Exception(() => new SqliteSignatureCentroidStore(
            RelativePath(), NullLogger<SqliteSignatureCentroidStore>.Instance));
        Assert.Null(ex);
    }

    [Fact]
    public void SessionCentroidStore_constructs_with_a_relative_databasepath()
    {
        var ex = Record.Exception(() => new SqliteSessionCentroidStore(
            RelativePath(), NullLogger<SqliteSessionCentroidStore>.Instance));
        Assert.Null(ex);
    }

    [Fact]
    public void IntentCentroidStore_constructs_with_a_relative_databasepath()
    {
        var ex = Record.Exception(() => new SqliteIntentCentroidStore(
            RelativePath(), NullLogger<SqliteIntentCentroidStore>.Instance));
        Assert.Null(ex);
    }
}
