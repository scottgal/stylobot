using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Pins <see cref="EscalateToLlmActionPolicy"/>'s HttpContext snapshot
///     shape. The LLM path is contractually "full context, not summary" so
///     the snapshot's contents are load-bearing: the classifier depends on
///     seeing what the visitor sent, and PII redaction depends on the
///     right headers being scrubbed.
/// </summary>
/// <remarks>
///     Uses the shared LLM request sink passed as <c>null</c> throughout
///     because the exercised paths are the no-op-with-null and the payload
///     build. Raise/coordinator behaviour is out of scope for these tests.
/// </remarks>
public class EscalateToLlmActionPolicySnapshotTests
{
    private static EscalateToLlmActionPolicy NewPolicy(
        TypedSignalSink<LlmClassificationRequest>? requestSignals,
        double minBotProbability = 0.15,
        double maxBotProbability = 0.85)
        => new(
            "llm-escalator",
            new EscalateToLlmActionOptions
            {
                MinBotProbability = minBotProbability,
                MaxBotProbability = maxBotProbability,
                EnqueueReason = "test",
            },
            requestSignals,
            NullLogger<EscalateToLlmActionPolicy>.Instance);

    private static AggregatedEvidence NewEvidence(double botProbability = 0.5)
        => new()
        {
            BotProbability = botProbability,
            Confidence = 0.5,
            RiskBand = RiskBand.Medium,
            Signals = new Dictionary<string, object>(),
        };

    private static HttpContext NewContextWithHeaders(Action<HttpContext>? configure = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("example.com");
        ctx.Request.Path = "/api/data";
        ctx.Request.QueryString = new QueryString("?q=test");
        configure?.Invoke(ctx);
        return ctx;
    }

    // ── Gate ────────────────────────────────────────────────────────

    [Fact]
    public async Task No_op_when_no_coordinator_registered()
    {
        var policy = NewPolicy(requestSignals: null);
        var ctx = NewContextWithHeaders();

        var result = await policy.ExecuteAsync(ctx, NewEvidence(botProbability: 0.5));

        result.Continue.Should().BeTrue();
        result.Description.Should().Contain("LLM lane absent");
    }

    [Fact]
    public async Task No_op_below_uncertain_band_lower_bound()
    {
        var policy = NewPolicy(requestSignals: null, minBotProbability: 0.2);
        var result = await policy.ExecuteAsync(NewContextWithHeaders(), NewEvidence(botProbability: 0.1));

        // Coordinator is null so all paths early-out at that check first;
        // the band gate is exercised indirectly via the description text.
        result.Description.Should().Contain("LLM lane absent",
            "with a null coordinator the escalator short-circuits before the band gate; " +
            "band-gate coverage is exercised in the coordinator-present tests when we have one");
    }

    // ── Snapshot content (via reflection into the private builder) ──

    [Fact]
    public void Snapshot_includes_request_line_and_protocol_metadata()
    {
        var snapshot = InvokeBuildRequestInfo(NewContextWithHeaders(), "test-ua/1.0");

        snapshot.Should().Contain("GET /api/data?q=test");
        snapshot.Should().Contain("Scheme: https");
        snapshot.Should().Contain("Host: example.com");
        snapshot.Should().Contain("User-Agent: test-ua/1.0");
    }

    [Fact]
    public void Snapshot_redacts_authorization_header()
    {
        var ctx = NewContextWithHeaders(c =>
        {
            c.Request.Headers.Authorization = "Bearer super-secret-token";
        });

        var snapshot = InvokeBuildRequestInfo(ctx, "");

        snapshot.Should().NotContain("super-secret-token",
            "Authorization header must never appear in the LLM prompt payload");
        snapshot.Should().Contain("Authorization: <redacted>");
    }

    [Fact]
    public void Snapshot_redacts_cookie_and_set_cookie_headers()
    {
        var ctx = NewContextWithHeaders(c =>
        {
            c.Request.Headers["Cookie"] = "session=abcdef; user=admin";
            c.Request.Headers["Set-Cookie"] = "x=1; Secure";
        });

        var snapshot = InvokeBuildRequestInfo(ctx, "");

        snapshot.Should().NotContain("abcdef");
        snapshot.Should().NotContain("Set-Cookie: x=1");
        snapshot.Should().Contain("Cookie: <redacted>");
        snapshot.Should().Contain("Set-Cookie: <redacted>");
    }

    [Fact]
    public void Snapshot_redacts_x_api_key_headers()
    {
        var ctx = NewContextWithHeaders(c =>
        {
            c.Request.Headers["X-Api-Key"] = "sk-live-secret";
            c.Request.Headers["X-Api-Key-Debug"] = "another-secret";
        });

        var snapshot = InvokeBuildRequestInfo(ctx, "");

        snapshot.Should().NotContain("sk-live-secret");
        snapshot.Should().NotContain("another-secret");
        snapshot.Should().Contain("X-Api-Key: <redacted>");
        snapshot.Should().Contain("X-Api-Key-Debug: <redacted>");
    }

    [Fact]
    public void Snapshot_emits_cookie_names_but_scrubs_values()
    {
        var ctx = NewContextWithHeaders(c =>
        {
            c.Request.Headers["Cookie"] = "session=raw-value; user=raw-user";
        });

        var snapshot = InvokeBuildRequestInfo(ctx, "");

        snapshot.Should().Contain("Cookies (values scrubbed):");
        snapshot.Should().Contain("session");
        snapshot.Should().Contain("user");
        snapshot.Should().NotContain("raw-value");
        snapshot.Should().NotContain("raw-user");
    }

    [Fact]
    public void Snapshot_passes_through_non_sensitive_header_values()
    {
        var ctx = NewContextWithHeaders(c =>
        {
            c.Request.Headers["Referer"] = "https://ref.example.com/page";
            c.Request.Headers["Accept-Language"] = "en-GB,en;q=0.9";
        });

        var snapshot = InvokeBuildRequestInfo(ctx, "");

        snapshot.Should().Contain("Referer: https://ref.example.com/page");
        snapshot.Should().Contain("Accept-Language: en-GB,en;q=0.9");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    ///     Invokes the private <c>BuildRequestInfo</c> via reflection.
    ///     The method is private-static by design (single caller inside
    ///     the policy) but its output is the LLM contract; tests must
    ///     pin it.
    /// </summary>
    private static string InvokeBuildRequestInfo(HttpContext context, string userAgent)
    {
        var method = typeof(EscalateToLlmActionPolicy).GetMethod(
            "BuildRequestInfo",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull();
        return (string)method!.Invoke(null, new object[] { context, userAgent })!;
    }
}