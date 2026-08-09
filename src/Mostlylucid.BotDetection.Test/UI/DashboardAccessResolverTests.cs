using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins the SECOND half of the SignalR outage (2026-08-09).
///
///     <para>
///         The first fix made the hub's auth LOGIC identical to the middleware's. It was
///         necessary and not sufficient, because the page and the hub live in DIFFERENT
///         PROCESSES with DIFFERENT <see cref="StyloBotDashboardOptions"/> instances: the
///         website resolved <c>STYLOBOT_DASHBOARD_PUBLIC</c> and configured auth properly,
///         while the gateway's <c>AddBotDetectionPersistence</c> registered a bare
///         <c>new StyloBotDashboardOptions { Enabled = true }</c>. Identical logic over
///         different configuration still produces different verdicts — every branch on the
///         gateway fell through to deny, so the hub aborted every connection.
///     </para>
///
///     <para>
///         <see cref="DashboardAccessResolver"/> is the one function both hosts now resolve
///         through, so the inputs match as well as the rules.
///     </para>
/// </summary>
public sealed class DashboardAccessResolverTests
{
    /// <summary>
    ///     THE STAGING CASE. Public dashboard configured, hub in a non-Development
    ///     environment. Before the fix this denied and killed live updates.
    /// </summary>
    [Fact]
    public async Task Public_dashboard_config_lets_the_hub_authorise_an_anonymous_client()
    {
        var options = Resolve(("StyloBotDashboard:Public", "true"));

        var allowed = await StyloBotDashboardHub.IsAuthorizedAsync(
            Context(), options, new ProdEnv());

        Assert.True(allowed,
            "a publicly-configured dashboard must let the hub connect — this is the staging "
            + "configuration whose hub aborted every connection");
    }

    /// <summary>
    ///     Parity guard: the resolver must not become a blanket allow. With the dashboard
    ///     NOT public, no secret and no principal, a non-Development host still denies.
    /// </summary>
    [Fact]
    public async Task Locked_dashboard_still_denies_an_anonymous_client()
    {
        var options = Resolve(("StyloBotDashboard:Public", "false"));

        var allowed = await StyloBotDashboardHub.IsAuthorizedAsync(
            Context(), options, new ProdEnv());

        Assert.False(allowed, "the hub must not become an unauthenticated side door");
    }

    /// <summary>
    ///     The access secret is a GET/HEAD affordance and is compared in constant time.
    ///     Documented explicitly because it is deliberately NOT a hub path: a SignalR
    ///     handshake POSTs to /negotiate, so the secret cannot admit a hub connection —
    ///     hub access comes from the public flag or a real principal.
    /// </summary>
    [Fact]
    public void Access_secret_is_accepted_on_GET_and_refused_on_POST()
    {
        const string secret = "s3cret-value";

        var get = Context();
        get.Request.Method = HttpMethods.Get;
        get.Request.Headers[DashboardAccessResolver.SecretHeader] = secret;
        Assert.True(DashboardAccessResolver.CarriesSecret(get, secret));

        var post = Context();
        post.Request.Method = HttpMethods.Post;
        post.Request.Headers[DashboardAccessResolver.SecretHeader] = secret;
        Assert.False(DashboardAccessResolver.CarriesSecret(post, secret));

        var wrong = Context();
        wrong.Request.Method = HttpMethods.Get;
        wrong.Request.Headers[DashboardAccessResolver.SecretHeader] = "not-the-secret";
        Assert.False(DashboardAccessResolver.CarriesSecret(wrong, secret));
    }

    /// <summary>
    ///     Environment variables are honoured when no config key is present — that is how
    ///     the deployed hosts are actually configured (STYLOBOT_DASHBOARD_PUBLIC).
    /// </summary>
    [Fact]
    public async Task Environment_variable_is_honoured_when_config_key_is_absent()
    {
        Environment.SetEnvironmentVariable(DashboardAccessResolver.PublicEnvVar, "true");
        try
        {
            var options = Resolve();   // no config keys at all

            var allowed = await StyloBotDashboardHub.IsAuthorizedAsync(
                Context(), options, new ProdEnv());

            Assert.True(allowed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DashboardAccessResolver.PublicEnvVar, null);
        }
    }

    private static StyloBotDashboardOptions Resolve(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

        return DashboardAccessResolver.ApplyAccessPosture(
            new StyloBotDashboardOptions { Enabled = true }, config, isDevelopment: false);
    }

    private static DefaultHttpContext Context()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private sealed class ProdEnv : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "test";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
