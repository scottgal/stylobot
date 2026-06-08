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

        var atDomain   = await store.GetRulesAtAsync(new PolicyScope.Domain("acme.com"));
        var atSub      = await store.GetRulesAtAsync(new PolicyScope.Subdomain("acme.com", "docs.acme.com"));
        var atEndpoint = await store.GetRulesAtAsync(new PolicyScope.Endpoint("acme.com", "docs.acme.com", "GET /api/upload"));

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
            new PolicyScope.Endpoint("acme.com", "docs.acme.com", "GET /api/upload"),
            new PolicyScope.Subdomain("acme.com", "docs.acme.com"),
            new PolicyScope.Domain("acme.com"),
            new PolicyScope.Wildcard()
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
            Assert.Equal(new PolicyScope.Domain("acme.com"), scope);
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
            var atWildcard = await store.GetRulesAtAsync(new PolicyScope.Wildcard());
            Assert.Single(atWildcard);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
