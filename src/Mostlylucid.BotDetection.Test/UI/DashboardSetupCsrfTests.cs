using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Direct coverage for the double-submit CSRF guard PR #29 added to the
///     first-run setup POST. Before this file the only proof the validation
///     fired was reading the diff; CSRF defences regress silently if no test
///     pegs the contract.
/// </summary>
public class DashboardSetupCsrfTests
{
    private const string ValidToken = "ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void Validate_RejectsWhenBothMissing()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Form = new FormCollection(new() { });
        Assert.False(StyloBotDashboardMiddleware.ValidateSetupCsrfToken(ctx));
    }

    [Fact]
    public void Validate_RejectsWhenOnlyCookieSet()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{StyloBotDashboardMiddleware.SetupCsrfCookieName}={ValidToken}";
        ctx.Request.Form = new FormCollection(new() { });
        Assert.False(StyloBotDashboardMiddleware.ValidateSetupCsrfToken(ctx));
    }

    [Fact]
    public void Validate_RejectsWhenOnlyFormSet()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Form = new FormCollection(new()
        {
            [StyloBotDashboardMiddleware.SetupCsrfFormField] = ValidToken,
        });
        Assert.False(StyloBotDashboardMiddleware.ValidateSetupCsrfToken(ctx));
    }

    [Fact]
    public void Validate_RejectsMismatchedCookieAndForm()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{StyloBotDashboardMiddleware.SetupCsrfCookieName}={ValidToken}";
        ctx.Request.Form = new FormCollection(new()
        {
            [StyloBotDashboardMiddleware.SetupCsrfFormField] =
                "ZZZZZZ0123456789ABCDEF0123456789",
        });
        Assert.False(StyloBotDashboardMiddleware.ValidateSetupCsrfToken(ctx));
    }

    [Fact]
    public void Validate_AcceptsMatchedCookieAndForm()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{StyloBotDashboardMiddleware.SetupCsrfCookieName}={ValidToken}";
        ctx.Request.Form = new FormCollection(new()
        {
            [StyloBotDashboardMiddleware.SetupCsrfFormField] = ValidToken,
        });
        Assert.True(StyloBotDashboardMiddleware.ValidateSetupCsrfToken(ctx));
    }

    [Fact]
    public void Validate_RejectsLengthMismatchSafely()
    {
        // The FixedTimeEquals path needs equal-length byte buffers; if either
        // side were shorter the comparator would throw without the guard. Peg
        // the contract that mismatched lengths return false (not throw).
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{StyloBotDashboardMiddleware.SetupCsrfCookieName}={ValidToken}";
        ctx.Request.Form = new FormCollection(new()
        {
            [StyloBotDashboardMiddleware.SetupCsrfFormField] = ValidToken.Substring(0, 8),
        });
        Assert.False(StyloBotDashboardMiddleware.ValidateSetupCsrfToken(ctx));
    }
}
