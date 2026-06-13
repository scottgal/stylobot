namespace Stylobot.Dashboard.NavigationTests.Fixtures;

/// <summary>
///     Resolves the base URL the navigation suite drives. Defaults to the
///     staging deployment so a `dotnet test` against this project is a real
///     end-to-end navigation audit against the same surface a deploy will
///     hit. Override via <c>STYLOBOT_NAV_BASE_URL</c> for local runs against
///     a dev host (e.g. <c>http://localhost:5062</c>).
/// </summary>
public static class TargetEnvironment
{
    public const string DefaultBaseUrl = "https://staging.stylobot.net";

    public static string BaseUrl
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("STYLOBOT_NAV_BASE_URL");
            return string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.TrimEnd('/');
        }
    }

    /// <summary>
    ///     Optional bypass token forwarded as <c>X-StyloBot-Bypass</c> so the
    ///     headless browser is admitted past gate policies that would otherwise
    ///     enforce a challenge against an unrecognised crawler. Set via
    ///     <c>STYLOBOT_NAV_BYPASS_KEY</c>; absent by default.
    /// </summary>
    public static string? BypassKey =>
        Environment.GetEnvironmentVariable("STYLOBOT_NAV_BYPASS_KEY");
}