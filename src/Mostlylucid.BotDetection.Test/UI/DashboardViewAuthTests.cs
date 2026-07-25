using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Services.Auth;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
/// Unit tests for the FOSS config-credential dashboard view-auth: the shared
/// password hasher (must round-trip so the CLI-generated hash verifies against a
/// login attempt) and the credential verifier that gates the login POST.
/// </summary>
public sealed class DashboardViewAuthTests
{
    [Fact]
    public void PasswordHasher_roundtrips_the_password_it_hashed()
    {
        var hash = DashboardPasswordHasher.Hash("s3cret-pw");

        Assert.True(DashboardPasswordHasher.Verify(hash, "s3cret-pw"));
    }

    [Fact]
    public void PasswordHasher_rejects_a_different_password()
    {
        var hash = DashboardPasswordHasher.Hash("s3cret-pw");

        Assert.False(DashboardPasswordHasher.Verify(hash, "wrong-pw"));
    }

    [Fact]
    public void Verifier_accepts_the_configured_username_and_password()
    {
        var options = LoginOptions("admin", "s3cret-pw");
        var verifier = new DashboardViewCredentialVerifier();

        Assert.True(verifier.Verify(options, "admin", "s3cret-pw"));
    }

    [Fact]
    public void Verifier_rejects_a_wrong_password()
    {
        var options = LoginOptions("admin", "s3cret-pw");
        var verifier = new DashboardViewCredentialVerifier();

        Assert.False(verifier.Verify(options, "admin", "nope"));
    }

    [Fact]
    public void Verifier_rejects_a_wrong_username()
    {
        var options = LoginOptions("admin", "s3cret-pw");
        var verifier = new DashboardViewCredentialVerifier();

        Assert.False(verifier.Verify(options, "attacker", "s3cret-pw"));
    }

    [Fact]
    public void Verifier_rejects_when_mode_is_not_login()
    {
        var options = LoginOptions("admin", "s3cret-pw");
        options.Mode = DashboardAuthMode.None;
        var verifier = new DashboardViewCredentialVerifier();

        Assert.False(verifier.Verify(options, "admin", "s3cret-pw"));
    }

    [Fact]
    public void Verifier_rejects_when_credential_is_unconfigured()
    {
        var options = new DashboardAuthOptions { Mode = DashboardAuthMode.Login };
        var verifier = new DashboardViewCredentialVerifier();

        Assert.False(verifier.Verify(options, "admin", "s3cret-pw"));
    }

    [Fact]
    public void Auth_section_binds_from_StyloBot_Dashboard_Auth()
    {
        var hash = DashboardPasswordHasher.Hash("s3cret-pw");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StyloBot:Dashboard:Auth:Mode"] = "Login",
                ["StyloBot:Dashboard:Auth:Username"] = "admin",
                ["StyloBot:Dashboard:Auth:PasswordHash"] = hash
            })
            .Build();

        var options = new StyloBotDashboardOptions();
        config.GetSection("StyloBot:Dashboard").Bind(options);

        Assert.Equal(DashboardAuthMode.Login, options.Auth.Mode);
        Assert.Equal("admin", options.Auth.Username);
        Assert.Equal(hash, options.Auth.PasswordHash);
        Assert.True(options.Auth.IsConfigured);
    }

    private static DashboardAuthOptions LoginOptions(string username, string password) => new()
    {
        Mode = DashboardAuthMode.Login,
        Username = username,
        PasswordHash = DashboardPasswordHasher.Hash(password)
    };
}
