using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     T4 Part A coverage: the SbScopePicker view-model + the JSON parser
///     on <see cref="PolicyScopeUrl.FromPickerJson"/>. The view component
///     itself is stateless -- it just returns the model -- so the wire
///     contract (JSON in / PolicyScope out) is what matters here.
/// </summary>
public sealed class SbScopePickerTests
{
    // -------- Default-methods sanity ----------------------------------

    [Fact]
    public void EffectiveMethods_falls_back_to_default_when_known_methods_null()
    {
        var vm = new ScopePickerViewModel();
        Assert.Same(ScopePickerViewModel.DefaultMethods, vm.EffectiveMethods);
        Assert.Contains("GET", vm.EffectiveMethods);
        Assert.Contains("POST", vm.EffectiveMethods);
    }

    [Fact]
    public void EffectiveMethods_uses_caller_list_when_supplied()
    {
        var methods = new[] { "GET", "DELETE" };
        var vm = new ScopePickerViewModel(KnownMethods: methods);
        Assert.Equal(methods, vm.EffectiveMethods);
    }

    // -------- Wildcard scope -------------------------------------------

    [Fact]
    public void FromPickerJson_null_or_empty_yields_wildcard()
    {
        Assert.True(PolicyScopeUrl.FromPickerJson(null).IsWildcard);
        Assert.True(PolicyScopeUrl.FromPickerJson(string.Empty).IsWildcard);
        Assert.True(PolicyScopeUrl.FromPickerJson("   ").IsWildcard);
    }

    [Fact]
    public void FromPickerJson_invalid_json_yields_wildcard()
    {
        Assert.True(PolicyScopeUrl.FromPickerJson("{ not valid").IsWildcard);
        Assert.True(PolicyScopeUrl.FromPickerJson("[]").IsWildcard);
    }

    [Fact]
    public void FromPickerJson_all_null_axes_yields_wildcard()
    {
        var json = """{"host":null,"method":null,"geo":null,"identity":null}""";
        Assert.True(PolicyScopeUrl.FromPickerJson(json).IsWildcard);
    }

    // -------- Host axis ------------------------------------------------

    [Fact]
    public void FromPickerJson_decodes_domain_host()
    {
        var json = """{"host":{"kind":"domain","domain":"acme.com"}}""";
        var scope = PolicyScopeUrl.FromPickerJson(json);
        var d = Assert.IsType<HostScope.Domain>(scope.Host);
        Assert.Equal("acme.com", d.Name);
        Assert.Null(scope.Method);
        Assert.Null(scope.Geo);
        Assert.Null(scope.Identity);
    }

    [Fact]
    public void FromPickerJson_decodes_subdomain_host()
    {
        var json = """{"host":{"kind":"subdomain","domain":"acme.com","sub":"docs.acme.com"}}""";
        var scope = PolicyScopeUrl.FromPickerJson(json);
        var s = Assert.IsType<HostScope.Subdomain>(scope.Host);
        Assert.Equal("acme.com", s.DomainName);
        Assert.Equal("docs.acme.com", s.SubdomainName);
    }

    [Fact]
    public void FromPickerJson_decodes_endpoint_host()
    {
        var json = """{"host":{"kind":"endpoint","domain":"acme.com","sub":"docs.acme.com","path":"GET /api/upload"}}""";
        var scope = PolicyScopeUrl.FromPickerJson(json);
        var e = Assert.IsType<HostScope.Endpoint>(scope.Host);
        Assert.Equal("acme.com", e.DomainName);
        Assert.Equal("docs.acme.com", e.SubdomainName);
        Assert.Equal("GET /api/upload", e.PathTemplate);
    }

    [Fact]
    public void FromPickerJson_host_missing_required_field_falls_back_to_null()
    {
        var json = """{"host":{"kind":"subdomain","domain":"acme.com"}}""";
        var scope = PolicyScopeUrl.FromPickerJson(json);
        Assert.Null(scope.Host);
    }

    [Fact]
    public void FromPickerJson_unknown_host_kind_falls_back_to_null()
    {
        var json = """{"host":{"kind":"region","name":"eu"}}""";
        var scope = PolicyScopeUrl.FromPickerJson(json);
        Assert.Null(scope.Host);
    }

    // -------- Method + Geo axes ---------------------------------------

    [Fact]
    public void FromPickerJson_method_and_geo_uppercase()
    {
        var json = """{"method":"post","geo":"us"}""";
        var scope = PolicyScopeUrl.FromPickerJson(json);
        Assert.Equal("POST", scope.Method);
        Assert.Equal("US", scope.Geo);
    }

    [Fact]
    public void FromPickerJson_empty_method_and_geo_null()
    {
        var json = """{"method":"","geo":""}""";
        var scope = PolicyScopeUrl.FromPickerJson(json);
        Assert.Null(scope.Method);
        Assert.Null(scope.Geo);
    }

    // -------- Identity axis -------------------------------------------

    [Theory]
    [InlineData("named_bot", typeof(IdentityScope.NamedBot))]
    [InlineData("bot_type", typeof(IdentityScope.BotType))]
    [InlineData("human_browser", typeof(IdentityScope.HumanBrowser))]
    [InlineData("fingerprint", typeof(IdentityScope.Fingerprint))]
    public void FromPickerJson_decodes_each_identity_kind(string kind, Type expected)
    {
        var json = "{\"identity\":{\"kind\":\"" + kind + "\",\"value\":\"acme\"}}";
        var scope = PolicyScopeUrl.FromPickerJson(json);
        Assert.NotNull(scope.Identity);
        Assert.IsType(expected, scope.Identity);
    }

    [Fact]
    public void FromPickerJson_identity_empty_value_yields_null()
    {
        var json = """{"identity":{"kind":"named_bot","value":""}}""";
        var scope = PolicyScopeUrl.FromPickerJson(json);
        Assert.Null(scope.Identity);
    }

    // -------- Full composite scope -----------------------------------

    [Fact]
    public void FromPickerJson_composite_scope_round_trips_through_all_four_axes()
    {
        var json = """
        {
          "host": { "kind": "subdomain", "domain": "acme.com", "sub": "api.acme.com" },
          "method": "POST",
          "geo": "RU",
          "identity": { "kind": "bot_type", "value": "scraper" }
        }
        """;
        var scope = PolicyScopeUrl.FromPickerJson(json);

        var s = Assert.IsType<HostScope.Subdomain>(scope.Host);
        Assert.Equal("acme.com", s.DomainName);
        Assert.Equal("api.acme.com", s.SubdomainName);
        Assert.Equal("POST", scope.Method);
        Assert.Equal("RU", scope.Geo);
        var bt = Assert.IsType<IdentityScope.BotType>(scope.Identity);
        Assert.Equal("scraper", bt.Category);
    }

    // -------- Specificity preserved ----------------------------------

    [Fact]
    public void FromPickerJson_specificity_matches_PolicyScope_factory()
    {
        var expected = new PolicyScope(
            Host: new HostScope.Endpoint("acme.com", "api.acme.com", "GET /upload"),
            Method: "POST",
            Geo: "US",
            Identity: new IdentityScope.NamedBot("googlebot"));
        var json = """
        {
          "host": { "kind": "endpoint", "domain": "acme.com", "sub": "api.acme.com", "path": "GET /upload" },
          "method": "POST",
          "geo": "US",
          "identity": { "kind": "named_bot", "value": "googlebot" }
        }
        """;
        var scope = PolicyScopeUrl.FromPickerJson(json);
        Assert.Equal(expected.Specificity, scope.Specificity);
    }
}
