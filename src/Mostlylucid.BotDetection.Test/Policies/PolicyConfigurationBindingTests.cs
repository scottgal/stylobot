using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using Mostlylucid.BotDetection.Policies;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies;

/// <summary>
///     Verifies that <see cref="DetectionPolicyConfig"/> binds the new
///     <c>OnFailure</c> and <c>LoadShed</c> properties from appsettings JSON,
///     and that <see cref="DetectionPolicyConfig.ToPolicy"/> correctly maps
///     them onto the immutable <see cref="DetectionPolicy"/> record.
/// </summary>
/// <remarks>
///     Customers do not call a dedicated <c>LoadFromConfiguration</c> entry point;
///     <c>BotDetectionOptions.Policies</c> is a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
///     bound by <c>BindConfiguration</c> via the standard <see cref="IConfiguration"/>
///     binder. These tests exercise that binder against a JSON stream and then call
///     <c>ToPolicy</c> directly, which is exactly what <c>PolicyRegistry</c> does at
///     runtime.
/// </remarks>
public class PolicyConfigurationBindingTests
{
    private static Dictionary<string, DetectionPolicyConfig> BindPolicies(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var policies = new Dictionary<string, DetectionPolicyConfig>();
        config.GetSection("Policies").Bind(policies);
        return policies;
    }

    [Fact]
    public void LoadFromJson_BindsFailureMode_AndLoadShed()
    {
        var json = """
        {
          "Policies": {
            "high-security": {
              "OnFailure": "FailClosed",
              "LoadShed": { "DropFractionAtHigh": 0.0, "DropFractionAtCritical": 0.1 }
            }
          }
        }
        """;

        var configs = BindPolicies(json);
        Assert.True(configs.ContainsKey("high-security"));
        var p = configs["high-security"].ToPolicy("high-security");

        Assert.Equal(FailureMode.FailClosed, p.OnFailure);
        Assert.Equal(0.0, p.LoadShed.DropFractionAtHigh);
        Assert.Equal(0.1, p.LoadShed.DropFractionAtCritical);
    }

    [Fact]
    public void LoadFromJson_DefaultsToFailOpen_WhenOnFailureAbsent()
    {
        var json = """
        {
          "Policies": {
            "default": { }
          }
        }
        """;

        var configs = BindPolicies(json);
        var p = configs["default"].ToPolicy("default");

        Assert.Equal(FailureMode.FailOpen, p.OnFailure);
        Assert.Equal(0.0, p.LoadShed.DropFractionAtCritical);
    }

    [Fact]
    public void LoadFromJson_UnrecognisedOnFailure_FallsBackToFailOpen()
    {
        var json = """
        {
          "Policies": {
            "bad-mode": { "OnFailure": "Garbage" }
          }
        }
        """;

        var configs = BindPolicies(json);
        var p = configs["bad-mode"].ToPolicy("bad-mode");

        Assert.Equal(FailureMode.FailOpen, p.OnFailure);
    }
}
