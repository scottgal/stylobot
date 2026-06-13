using System.IO;
using System.Linq;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression guard for the navigation bug shipped on 2026-06-12 in
///     34e2d747. The middleware's <c>signature/</c> case was rewritten to
///     302-redirect <c>/dashboard/signature/&lt;id&gt;</c> to
///     <c>/dashboard/endpoints?selectedSig=&lt;id&gt;</c> on the premise
///     that the endpoints page would consume <c>selectedSig</c> and render
///     the signature detail inline. The consumer was never wired, so every
///     fingerprint click landed on a bare endpoint list with no detail.
///
///     Source-level pins because the middleware has a heavy DI surface and
///     a TestServer integration test would be a disproportionate scaffold
///     for a 5-line route handler. If anyone reintroduces the redirect (or
///     wires a new consumer somewhere else), these tests fail loudly.
/// </summary>
public class DashboardSignatureRouteRegressionTests
{
    private const string MiddlewareRelativePath =
        "src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs";

    [Fact]
    public void Signature_route_does_not_redirect_to_endpoints_page()
    {
        var src = ReadMiddlewareSource();
        Assert.DoesNotContain("endpoints?selectedSig", src);
    }

    [Fact]
    public void Signature_route_dispatches_directly_to_ServeSignatureDetailAsync()
    {
        var src = ReadMiddlewareSource();

        // Anchor the assertion on the signature/ case block specifically so a
        // matching call elsewhere (e.g. partials/signature-detail) cannot
        // accidentally satisfy this guard.
        var caseIndex = src.IndexOf("StartsWith(\"signature/\"", System.StringComparison.Ordinal);
        Assert.True(caseIndex >= 0, "signature/ case missing from middleware");

        var nextCaseIndex = src.IndexOf("case ", caseIndex + 1, System.StringComparison.Ordinal);
        Assert.True(nextCaseIndex > caseIndex, "could not locate end of signature/ case block");

        var caseBlock = src[caseIndex..nextCaseIndex];
        Assert.Contains("ServeSignatureDetailAsync", caseBlock);
        Assert.DoesNotContain("Response.Redirect", caseBlock);
    }

    private static string ReadMiddlewareSource()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, MiddlewareRelativePath)))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, MiddlewareRelativePath));
    }
}