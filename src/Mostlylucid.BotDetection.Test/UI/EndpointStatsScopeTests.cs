using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Dashboard.Data.Queries;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public sealed class EndpointStatsScopeTests
{
    [Fact]
    public void Internal_request_on_a_store_without_internal_endpoints_is_unavailable()
    {
        Assert.Equal(EndpointStatsScope.InternalUnavailable,
            EndpointStatsScope.Resolve(true, false, "all_incl_internal", new DateTime(2026, 1, 1), new DateTime(2026, 1, 2)));
        Assert.Equal(EndpointStatsScope.InternalUnavailable,
            EndpointStatsScope.Resolve(true, false, "internal", null, null));
        Assert.Equal(EndpointStatsScope.RetainedWindow,
            EndpointStatsScope.Resolve(false, true, "all_incl_internal", new DateTime(2026, 1, 1), new DateTime(2026, 1, 2)));
    }

    [Fact]
    public void Windowed_store_is_windowed_for_every_audience()
    {
        Assert.Equal(EndpointStatsScope.Windowed,
            EndpointStatsScope.Resolve(true, "all", new DateTime(2026, 1, 1), new DateTime(2026, 1, 2)));
        Assert.Equal(EndpointStatsScope.Windowed,
            EndpointStatsScope.Resolve(true, true, "all_incl_internal", null, null));
    }

    [Fact]
    public void Lifetime_store_default_audiences_are_lifetime()
    {
        Assert.Equal(EndpointStatsScope.Lifetime,
            EndpointStatsScope.Resolve(false, "all", new DateTime(2026, 1, 1), new DateTime(2026, 1, 2)));
        Assert.Equal(EndpointStatsScope.Lifetime,
            EndpointStatsScope.Resolve(false, "honeypot", new DateTime(2026, 1, 1), new DateTime(2026, 1, 2)));
    }

    [Theory]
    [InlineData("all_incl_internal")]
    [InlineData("internal")]
    public void Lifetime_store_internal_opt_in_with_a_window_is_retained_window(string audience)
    {
        Assert.Equal(EndpointStatsScope.RetainedWindow,
            EndpointStatsScope.Resolve(false, true, audience, new DateTime(2026, 1, 1), new DateTime(2026, 1, 2)));
    }

    [Fact]
    public void Lifetime_store_internal_opt_in_without_a_window_is_lifetime()
    {
        Assert.Equal(EndpointStatsScope.Lifetime,
            EndpointStatsScope.Resolve(false, true, "all_incl_internal", null, null));
    }

    [Fact]
    public void Legacy_bundle_with_only_the_capability_still_exposes_windowed()
    {
        var legacy = new DashboardPageResult(new DashboardDatasetBundle(
            null, null, null, null, null, EndpointStatsWindowed: true));

        Assert.Null(legacy.EndpointStatsScope);
        Assert.True(legacy.EndpointStatsWindowed == true);
    }

    [Fact]
    public void Request_item_read_is_null_when_unset_and_set_stamps_truth()
    {
        var context = new DefaultHttpContext();
        Assert.Null(EndpointStatsScope.ReadOrNull(context));

        EndpointStatsScope.Set(context, false, true, "all_incl_internal",
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 2));
        Assert.Equal(EndpointStatsScope.RetainedWindow, EndpointStatsScope.ReadOrNull(context));
        Assert.Equal(EndpointStatsScope.RetainedWindow,
            context.Items[EndpointStatsScope.ItemsKey] as string);
        Assert.Equal(EndpointStatsScope.Lifetime, EndpointStatsScope.Normalize("not-a-scope"));
        Assert.Equal(EndpointStatsScope.Windowed, EndpointStatsScope.Normalize("WINDOWED"));

        EndpointStatsScope.Set(context, true, false, "all_incl_internal", null, null);
        Assert.Equal(EndpointStatsScope.InternalUnavailable, EndpointStatsScope.ReadOrNull(context));
    }
}
