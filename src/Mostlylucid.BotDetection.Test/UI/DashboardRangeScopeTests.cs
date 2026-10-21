using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.UI.Helpers;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     One canonical window resolver for the dashboard. Duration tokens keep their
///     existing behaviour; a custom range resolves its explicit from/to pair instead
///     of parsing "custom" as a duration (which returns null and silently widens a
///     read to the widget's default).
/// </summary>
public sealed class DashboardRangeScopeTests
{
    private static HttpContext WithQuery(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    [Fact]
    public void Custom_range_resolves_the_explicit_pair()
    {
        var context = WithQuery("?window=custom&from=2026-10-01&to=2026-10-05");

        var (start, end) = DashboardRangeScope.Resolve(context, "custom");

        Assert.NotNull(start);
        Assert.NotNull(end);
        Assert.Equal(new DateTime(2026, 10, 1), start!.Value.Date);
        // ParseCustom treats the to-date as inclusive, so the end is the next midnight.
        Assert.Equal(new DateTime(2026, 10, 6), end!.Value.Date);
        Assert.True(end > start);
    }

    [Fact]
    public void Duration_token_keeps_the_parser_contract()
    {
        var (start, end) = DashboardRangeScope.Resolve(WithQuery("?window=7d"), "7d");

        Assert.NotNull(start);
        Assert.NotNull(end);
        Assert.Equal(TimeSpan.FromDays(7), end!.Value - start!.Value);
    }

    [Fact]
    public void Unparseable_custom_pair_returns_no_window_so_callers_keep_their_default()
    {
        var (start, end) = DashboardRangeScope.Resolve(WithQuery("?window=custom"), "custom");

        Assert.Null(start);
        Assert.Null(end);
    }
}
