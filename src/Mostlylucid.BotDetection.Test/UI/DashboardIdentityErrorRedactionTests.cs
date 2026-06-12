using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     The first-run setup POST surfaces ASP.NET Core Identity error
///     descriptions back to the operator via a redirect query string.
///     Identity errors can quote the submitted email verbatim
///     ("Username 'a@b.com' is already taken."); without scrubbing, the
///     address ends up in access logs, browser history, and Referer headers
///     to any third-party CSS/JS the setup page loads.
/// </summary>
public class DashboardIdentityErrorRedactionTests
{
    [Fact]
    public void Redact_RemovesEmailFromTypicalIdentityMessage()
    {
        var redacted = StyloBotDashboardMiddleware.RedactEmailFromIdentityErrors(
            ["Username 'admin@stylobot.net' is already taken."],
            "admin@stylobot.net");
        Assert.DoesNotContain("admin@stylobot.net", redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Fact]
    public void Redact_HandlesMultipleErrorsAndJoinsThem()
    {
        var redacted = StyloBotDashboardMiddleware.RedactEmailFromIdentityErrors(
            [
                "Username 'admin@stylobot.net' is already taken.",
                "Email 'admin@stylobot.net' is invalid.",
            ],
            "admin@stylobot.net");
        Assert.DoesNotContain("admin@stylobot.net", redacted);
        // join character preserved
        Assert.Contains(", ", redacted);
    }

    [Fact]
    public void Redact_IsCaseInsensitive()
    {
        // Identity normalises emails to upper-case for storage, but the
        // description may quote the original-case input.
        var redacted = StyloBotDashboardMiddleware.RedactEmailFromIdentityErrors(
            ["Email 'ADMIN@STYLOBOT.NET' is already taken."],
            "admin@stylobot.net");
        Assert.DoesNotContain("ADMIN", redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Fact]
    public void Redact_PassesThroughWhenDescriptionDoesNotContainEmail()
    {
        var redacted = StyloBotDashboardMiddleware.RedactEmailFromIdentityErrors(
            ["Password is too short."],
            "admin@stylobot.net");
        Assert.Equal("Password is too short.", redacted);
    }

    [Fact]
    public void Redact_HandlesEmptyEmailGracefully()
    {
        // Edge: empty email would otherwise become "all chars replaced" via
        // String.Replace("", "[redacted]"). Guard returns descriptions as-is.
        var redacted = StyloBotDashboardMiddleware.RedactEmailFromIdentityErrors(
            ["Password is too short."],
            "");
        Assert.Equal("Password is too short.", redacted);
    }
}
