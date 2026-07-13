using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Extensions;

/// <summary>
///     Pins the API-key learning-suppression gate consumed by both detection
///     orchestrators. The contract: when the validated <see cref="ApiKeyContext"/>
///     carries <c>DisableLearningWrites = true</c>, the orchestrator skips
///     Markov transition recording, background enrichment enqueue, and any other
///     downstream learning write. Detection still runs and the response header
///     trail is honest; only write-back into the model is suppressed.
/// </summary>
public sealed class IsLearningSuppressedByApiKeyTests
{
    private static ApiKeyContext NewContext(
        bool disableLearning,
        bool allowImpersonation = false,
        string? boundIdentity = null)
        => new()
        {
            KeyName = "test",
            DisabledDetectors = Array.Empty<string>(),
            WeightOverrides = new Dictionary<string, double>(),
            DisableLearningWrites = disableLearning,
            AllowImpersonation = allowImpersonation,
            BoundIdentity = boundIdentity,
        };

    [Fact]
    public void Returns_false_when_no_api_key_attached()
    {
        var ctx = new DefaultHttpContext();
        Assert.False(ctx.IsLearningSuppressedByApiKey());
    }

    [Fact]
    public void Returns_false_when_api_key_does_not_disable_learning()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["BotDetection.ApiKeyContext"] = NewContext(disableLearning: false);
        Assert.False(ctx.IsLearningSuppressedByApiKey());
    }

    [Fact]
    public void Returns_true_when_api_key_disables_learning()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["BotDetection.ApiKeyContext"] = NewContext(disableLearning: true);
        Assert.True(ctx.IsLearningSuppressedByApiKey());
    }

    [Fact]
    public void Legacy_bypass_key_does_not_suppress_learning()
    {
        // Legacy bypass keys stash a sentinel but no rich ApiKeyContext.
        // Suppression is opt-in via the rich-key DisableLearningWrites flag;
        // legacy keys keep recording (they're full-bypass anyway, so there's
        // little real-traffic shape to learn from -- but the contract is
        // explicit so the suppression gate doesn't accidentally widen).
        var ctx = new DefaultHttpContext();
        ctx.Items["BotDetection.ApiKeyBypass"] = true;
        Assert.False(ctx.IsLearningSuppressedByApiKey());
    }

    // ── Impersonation ────────────────────────────────────────────────────────

    [Fact]
    public void No_impersonation_when_key_lacks_the_capability()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["BotDetection.ApiKeyContext"] = NewContext(false, allowImpersonation: false, boundIdentity: "sig-victim");
        ctx.Request.Headers[HttpContextExtensions.ImpersonateHeader] = "sig-header";
        // Capability gate: neither the header nor BoundIdentity is honoured without AllowImpersonation.
        Assert.Null(ctx.GetImpersonationTarget());
        Assert.False(ctx.IsImpersonating());
    }

    [Fact]
    public void Impersonation_header_wins_when_capability_granted()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["BotDetection.ApiKeyContext"] = NewContext(false, allowImpersonation: true, boundIdentity: "sig-default");
        ctx.Request.Headers[HttpContextExtensions.ImpersonateHeader] = "sig-header";
        Assert.Equal("sig-header", ctx.GetImpersonationTarget());
    }

    [Fact]
    public void Impersonation_falls_back_to_bound_identity_when_no_header()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["BotDetection.ApiKeyContext"] = NewContext(false, allowImpersonation: true, boundIdentity: "sig-default");
        Assert.Equal("sig-default", ctx.GetImpersonationTarget());
    }

    [Fact]
    public void Impersonation_null_when_capable_but_no_target_supplied()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["BotDetection.ApiKeyContext"] = NewContext(false, allowImpersonation: true, boundIdentity: null);
        Assert.Null(ctx.GetImpersonationTarget());
    }

    [Fact]
    public void Impersonation_forces_learning_suppression_even_when_key_allows_learning()
    {
        // The key itself has DisableLearningWrites=false, but an active impersonation must never
        // write to the impersonated target's model -- suppression is forced on.
        var ctx = new DefaultHttpContext();
        ctx.Items["BotDetection.ApiKeyContext"] = NewContext(disableLearning: false, allowImpersonation: true, boundIdentity: "sig-victim");
        Assert.True(ctx.IsImpersonating());
        Assert.True(ctx.IsLearningSuppressedByApiKey());
    }
}