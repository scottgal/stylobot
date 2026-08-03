using System.Collections.Generic;
using System.Threading.Tasks;
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

    [Fact]
    public void PasswordHasher_rejects_null_hash()
    {
        Assert.False(DashboardPasswordHasher.Verify(null, "pw"));
    }

    [Fact]
    public void PasswordHasher_rejects_null_password()
    {
        var hash = DashboardPasswordHasher.Hash("pw");
        Assert.False(DashboardPasswordHasher.Verify(hash, null));
    }

    [Fact]
    public void PasswordHasher_rejects_empty_password()
    {
        var hash = DashboardPasswordHasher.Hash("pw");
        Assert.False(DashboardPasswordHasher.Verify(hash, ""));
    }

    [Fact]
    public void PasswordHasher_produces_different_hashes_for_different_passwords()
    {
        var h1 = DashboardPasswordHasher.Hash("alpha");
        var h2 = DashboardPasswordHasher.Hash("beta");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void Verifier_rejects_null_options()
    {
        var verifier = new DashboardViewCredentialVerifier();
        Assert.False(verifier.Verify(null!, "admin", "pw"));
    }

    [Fact]
    public void Verifier_rejects_empty_username()
    {
        var options = LoginOptions("admin", "s3cret-pw");
        var verifier = new DashboardViewCredentialVerifier();
        Assert.False(verifier.Verify(options, "", "s3cret-pw"));
    }

    [Fact]
    public void Verifier_rejects_empty_password()
    {
        var options = LoginOptions("admin", "s3cret-pw");
        var verifier = new DashboardViewCredentialVerifier();
        Assert.False(verifier.Verify(options, "admin", ""));
    }

    // ---- DashboardAuthPosture advisory ----

    [Fact]
    public void Advisory_returns_null_when_login_is_fully_configured()
    {
        var options = new StyloBotDashboardOptions
        {
            Auth =
            {
                Mode = DashboardAuthMode.Login,
                Username = "admin",
                PasswordHash = DashboardPasswordHasher.Hash("pw")
            }
        };
        Assert.Null(DashboardAuthPosture.Advisory(options));
    }

    [Fact]
    public void Advisory_warns_when_login_mode_but_no_credential()
    {
        var options = new StyloBotDashboardOptions
        {
            Auth = { Mode = DashboardAuthMode.Login }
        };
        var advisory = DashboardAuthPosture.Advisory(options);
        Assert.NotNull(advisory);
        Assert.Contains("Login but Username/PasswordHash are not both set", advisory);
    }

    [Fact]
    public void Advisory_warns_when_no_auth_configured_at_all()
    {
        var options = new StyloBotDashboardOptions();
        var advisory = DashboardAuthPosture.Advisory(options);
        Assert.NotNull(advisory);
        Assert.Contains("no view-auth configured", advisory);
    }

    [Fact]
    public void Advisory_returns_null_when_allow_unauth_is_explicitly_true()
    {
        var options = new StyloBotDashboardOptions { AllowUnauthenticatedAccess = true };
        Assert.Null(DashboardAuthPosture.Advisory(options));
    }

    [Fact]
    public void Advisory_returns_null_when_require_authentication_is_true()
    {
        var options = new StyloBotDashboardOptions { RequireAuthentication = true };
        Assert.Null(DashboardAuthPosture.Advisory(options));
    }

    [Fact]
    public void Advisory_returns_null_when_authorization_filter_is_set()
    {
        var options = new StyloBotDashboardOptions
        {
            AuthorizationFilter = _ => Task.FromResult(true)
        };
        Assert.Null(DashboardAuthPosture.Advisory(options));
    }

    [Fact]
    public void Advisory_returns_null_when_authorization_policy_is_set()
    {
        var options = new StyloBotDashboardOptions { RequireAuthorizationPolicy = "AdminOnly" };
        Assert.Null(DashboardAuthPosture.Advisory(options));
    }

    // ---- DashboardAuthOptions.IsConfigured ----

    [Fact]
    public void IsConfigured_false_when_mode_is_none()
    {
        var options = new DashboardAuthOptions
        {
            Mode = DashboardAuthMode.None,
            Username = "admin",
            PasswordHash = DashboardPasswordHasher.Hash("pw")
        };
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_false_when_password_hash_missing()
    {
        var options = new DashboardAuthOptions
        {
            Mode = DashboardAuthMode.Login,
            Username = "admin"
        };
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_false_when_username_missing()
    {
        var options = new DashboardAuthOptions
        {
            Mode = DashboardAuthMode.Login,
            PasswordHash = DashboardPasswordHasher.Hash("pw")
        };
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void CookieName_defaults_to_sb_dashboard_auth()
    {
        var options = new DashboardAuthOptions();
        Assert.Equal("sb.dashboard.auth", options.CookieName);
    }

    [Fact]
    public void SlidingExpirationMinutes_defaults_to_480()
    {
        var options = new DashboardAuthOptions();
        Assert.Equal(480, options.SlidingExpirationMinutes);
    }

    private static DashboardAuthOptions LoginOptions(string username, string password) => new()
    {
        Mode = DashboardAuthMode.Login,
        Username = username,
        PasswordHash = DashboardPasswordHasher.Hash(password)
    };
}
