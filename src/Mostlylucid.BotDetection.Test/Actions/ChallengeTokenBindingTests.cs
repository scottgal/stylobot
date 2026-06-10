using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Pins the challenge-token binding contract. The original
///     <see cref="ChallengeActionPolicy"/> bound the solved-cookie to
///     <see cref="SignalKeys.PrimarySignature"/>, which means any drift in
///     the foundation-signal mix between two consecutive page loads
///     invalidates the cookie and re-presents the challenge. Behind a
///     reverse proxy that doesn't (or can't) forward the real TLS / HTTP/2
///     handshake the foundation signals are deterministic-but-blank, and
///     anything that nudges the rest of the binding (a new app cookie, a
///     navigation change in Sec-Fetch-Site, an extra header on an XHR vs
///     a document load) flips PrimarySignature and the user gets re-PoW'd
///     on every click.
///
///     Stable binding mode hashes only the User-Agent and treats the rest
///     of the visitor identity as out-of-scope for the cookie. The cookie
///     remains host-scoped via the existing
///     <c>Cookies.Append(... HttpOnly = true, Secure = true, SameSite =
///     Strict)</c> wire-up, so cross-origin replay is still blocked.
/// </summary>
public class ChallengeTokenBindingTests
{
    [Fact]
    public void Strict_TokenIssuedWithSigA_RejectedWhenSigDrifts()
    {
        // Backwards-compat path: when TokenBindingMode = Strict, a token
        // issued against PrimarySignature "sig:A" must fail verification
        // when the same visitor's PrimarySignature drifts to "sig:B" on
        // the next request. This is the original behaviour and the
        // surprise that drove the move to Stable.
        var opts = new ChallengeActionOptions
        {
            UseTokens = true,
            TokenBindingMode = ChallengeTokenBindingMode.Strict,
            TokenSecret = "test-secret-at-least-32-chars-long-x",
        };
        var ctxIssue = NewContext("Mozilla/5.0 Chrome/148", evidenceSignature: "sig:A");
        var token = ChallengeActionPolicy.GenerateChallengeToken(ctxIssue, opts);

        var ctxVerify = NewContext("Mozilla/5.0 Chrome/148", evidenceSignature: "sig:B");
        ctxVerify.Request.Headers.Cookie = $"{opts.TokenCookieName}={token}";

        var policy = new ChallengeActionPolicy("challenge-pow", opts);
        Assert.False(
            InvokeHasValidChallengeToken(policy, ctxVerify),
            "Strict mode must reject the cookie when PrimarySignature drifts");
    }

    [Fact]
    public void Stable_TokenIssuedWithSigA_AcceptedWhenSigDriftsButUaStable()
    {
        // The headline fix: Stable mode binds to the UA only, so the same
        // visitor on the same browser keeps their solved-cookie even when
        // the rest of the foundation signals shift between requests.
        var opts = new ChallengeActionOptions
        {
            UseTokens = true,
            TokenBindingMode = ChallengeTokenBindingMode.Stable,
            TokenSecret = "test-secret-at-least-32-chars-long-x",
        };
        var ctxIssue = NewContext("Mozilla/5.0 Chrome/148", evidenceSignature: "sig:A");
        var token = ChallengeActionPolicy.GenerateChallengeToken(ctxIssue, opts);

        var ctxVerify = NewContext("Mozilla/5.0 Chrome/148", evidenceSignature: "sig:B");
        ctxVerify.Request.Headers.Cookie = $"{opts.TokenCookieName}={token}";

        var policy = new ChallengeActionPolicy("challenge-pow", opts);
        Assert.True(
            InvokeHasValidChallengeToken(policy, ctxVerify),
            "Stable mode must accept the cookie when only PrimarySignature drifts");
    }

    [Fact]
    public void Stable_TokenIssuedWithChromeUa_RejectedWhenUaChanges()
    {
        // Stable mode still must reject the cookie if the User-Agent itself
        // changes -- a different browser is a different visitor identity.
        // This is the line below which Stable would be useless.
        var opts = new ChallengeActionOptions
        {
            UseTokens = true,
            TokenBindingMode = ChallengeTokenBindingMode.Stable,
            TokenSecret = "test-secret-at-least-32-chars-long-x",
        };
        var ctxIssue = NewContext("Mozilla/5.0 Chrome/148", evidenceSignature: "sig:A");
        var token = ChallengeActionPolicy.GenerateChallengeToken(ctxIssue, opts);

        var ctxVerify = NewContext("Mozilla/5.0 Firefox/130", evidenceSignature: "sig:A");
        ctxVerify.Request.Headers.Cookie = $"{opts.TokenCookieName}={token}";

        var policy = new ChallengeActionPolicy("challenge-pow", opts);
        Assert.False(
            InvokeHasValidChallengeToken(policy, ctxVerify),
            "Stable mode must reject the cookie when the UA itself changes");
    }

    [Fact]
    public void Stable_IsTheDefault()
    {
        // Default must be Stable so existing deployments behind reverse
        // proxies stop re-challenging real users on every page load. The
        // backwards-compat Strict mode is opt-in.
        var opts = new ChallengeActionOptions();
        Assert.Equal(ChallengeTokenBindingMode.Stable, opts.TokenBindingMode);
    }

    // === Helpers ===========================================================

    private static HttpContext NewContext(string userAgent, string evidenceSignature)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("www.example.com");
        ctx.Request.Path = "/";
        ctx.Request.Headers.UserAgent = userAgent;
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.5");

        // Park an AggregatedEvidence with the requested signature so
        // ResolveRequestBinding finds it on the Items dict.
        var evidence = new AggregatedEvidence
        {
            Ledger = new DetectionLedger("test"),
            BotProbability = 0.9,
            Confidence = 1.0,
            RiskBand = RiskBand.High,
            CategoryBreakdown = new Dictionary<string, CategoryScore>(),
            Signals = new Dictionary<string, object>
            {
                [SignalKeys.PrimarySignature] = evidenceSignature,
            }.ToImmutableDictionary(),
            ContributingDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
        };
        ctx.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;
        return ctx;
    }

    /// <summary>
    ///     <c>HasValidChallengeToken</c> is private; the test reaches into
    ///     it via reflection so the contract can be pinned without exposing
    ///     the method on the public surface.
    /// </summary>
    private static bool InvokeHasValidChallengeToken(ChallengeActionPolicy policy, HttpContext ctx)
    {
        var method = typeof(ChallengeActionPolicy).GetMethod(
            "HasValidChallengeToken",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (bool)method.Invoke(policy, new object[] { ctx })!;
    }
}
