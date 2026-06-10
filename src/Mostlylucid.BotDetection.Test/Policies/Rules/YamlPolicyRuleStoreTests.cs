using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Rules;

public class YamlPolicyRuleStoreTests
{
    private const string SeedPrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";

    [Fact]
    public async Task Embedded_store_loads_three_seed_rules()
    {
        var store = YamlPolicyRuleStore.FromEmbeddedResources(
            typeof(PolicyRule).Assembly,
            SeedPrefix);
        await store.InitializeAsync();

        var atDomain   = await store.GetRulesAtAsync(PolicyScope.Domain("acme.com"));
        var atSub      = await store.GetRulesAtAsync(PolicyScope.Subdomain("acme.com", "docs.acme.com"));
        var atEndpoint = await store.GetRulesAtAsync(PolicyScope.Endpoint("acme.com", "docs.acme.com", "GET /api/upload"));

        Assert.Contains(atDomain,   r => r.Predicate is Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term { Facet: "is_human" });
        Assert.Contains(atSub,      r => r.Action is PolicyAction.Challenge);
        Assert.Contains(atEndpoint, r => r.Action is PolicyAction.Block);
    }

    [Fact]
    public async Task Effective_rules_path_returns_most_specific_first()
    {
        var store = YamlPolicyRuleStore.FromEmbeddedResources(
            typeof(PolicyRule).Assembly,
            SeedPrefix);
        await store.InitializeAsync();

        var path = new PolicyScope[]
        {
            PolicyScope.Endpoint("acme.com", "docs.acme.com", "GET /api/upload"),
            PolicyScope.Subdomain("acme.com", "docs.acme.com"),
            PolicyScope.Domain("acme.com"),
            PolicyScope.Wildcard()
        };
        var effective = await store.GetEffectiveRulesAsync(path);

        Assert.NotEmpty(effective);
        Assert.True(effective[0].Action is PolicyAction.Block,
            "endpoint-level block rule must win priority ordering");
    }

    [Fact]
    public async Task File_watcher_reloads_on_change()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-store-test").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: 44444444-4444-4444-4444-444444444444
                scope:
                  kind: domain
                  domain: acme.com
                priority: 100
                predicate: "is_human = true"
                action:
                  kind: allow
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();

            var fired = new TaskCompletionSource<PolicyScope>();
            store.Changed += (_, e) => fired.TrySetResult(e.ChangedScope);

            // Give the watcher a brief moment to settle before writing the change
            // so the debounce window doesn't swallow the rewrite.
            await Task.Delay(350);

            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: 44444444-4444-4444-4444-444444444444
                scope:
                  kind: domain
                  domain: acme.com
                priority: 50
                predicate: "is_human = true"
                action:
                  kind: block
                mode: live
                notes: ""
                """);

            var scope = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PolicyScope.Domain("acme.com"), scope);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task Bad_yaml_file_is_skipped_with_log_not_crash()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-store-test").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "broken.yaml"), "this is not yaml: ::");
            await File.WriteAllTextAsync(Path.Combine(tmp, "good.yaml"),
                """
                id: 55555555-5555-5555-5555-555555555555
                scope:
                  kind: wildcard
                priority: 10
                predicate: "is_human = true"
                action:
                  kind: allow
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();   // must not throw
            var atWildcard = await store.GetRulesAtAsync(PolicyScope.Wildcard());
            Assert.Single(atWildcard);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // -------- Composite-shape back-compat + new schema --------

    [Fact]
    public async Task Backcompat_legacy_wildcard_kind_parses_to_composite_wildcard()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-store-test").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: 66666666-6666-6666-6666-666666666666
                scope:
                  kind: wildcard
                priority: 100
                predicate: "is_human = true"
                action:
                  kind: allow
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();
            var atWildcard = await store.GetRulesAtAsync(PolicyScope.Wildcard());
            Assert.Single(atWildcard);
            Assert.True(atWildcard[0].Scope.IsWildcard);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task Backcompat_legacy_domain_kind_parses_to_host_domain_scope()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-store-test").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: 77777777-7777-7777-7777-777777777777
                scope:
                  kind: domain
                  domain: acme.com
                priority: 100
                predicate: "is_human = true"
                action:
                  kind: allow
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();
            var atDomain = await store.GetRulesAtAsync(PolicyScope.Domain("acme.com"));
            Assert.Single(atDomain);
            Assert.IsType<HostScope.Domain>(atDomain[0].Scope.Host);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task New_schema_composite_method_geo_parses_with_those_slots_populated()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-store-test").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: 88888888-8888-8888-8888-888888888888
                scope:
                  method: POST
                  geo: RU
                priority: 100
                predicate: "is_human = true"
                action:
                  kind: allow
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();
            var rules = await store.GetEffectiveRulesAsync(new[] { PolicyScope.Wildcard() });
            Assert.Single(rules);
            Assert.Null(rules[0].Scope.Host);
            Assert.Equal("POST", rules[0].Scope.Method);
            Assert.Equal("RU", rules[0].Scope.Geo);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task New_schema_composite_host_endpoint_plus_identity_parses_correctly()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-store-test").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: 99999999-9999-9999-9999-999999999999
                scope:
                  host:
                    kind: endpoint
                    domain: acme.com
                    subdomain: docs.acme.com
                    path_template: "GET /api/upload"
                  identity:
                    kind: named_bot
                    family: googlebot
                priority: 50
                predicate: "is_human = true"
                action:
                  kind: block
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();
            var rules = await store.GetEffectiveRulesAsync(new[]
            {
                PolicyScope.Endpoint("acme.com", "docs.acme.com", "GET /api/upload")
            });
            Assert.Single(rules);
            var scope = rules[0].Scope;
            Assert.IsType<HostScope.Endpoint>(scope.Host);
            var ident = Assert.IsType<IdentityScope.NamedBot>(scope.Identity);
            Assert.Equal("googlebot", ident.Family);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
