using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.Test.Policies.Templates;

/// <summary>
///     Loader-level tests for <see cref="YamlTemplateStore"/>. Pins the
///     embedded-catalog round-trip and the customer-directory layering.
/// </summary>
public class YamlTemplateStoreTests
{
    [Fact]
    public void Embedded_catalog_loads_every_yaml_without_error()
    {
        var store = new YamlTemplateStore();

        var templates = store.LoadEmbeddedCatalog();

        // The catalog ships ten seed templates -- if anyone adds another
        // YAML file without registering it as an EmbeddedResource the assert
        // tells you immediately.
        Assert.Equal(10, templates.Count);
        Assert.All(templates, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Id));
            Assert.NotEmpty(t.Expansion);
        });
    }

    [Fact]
    public void Loaded_template_expansion_count_matches_each_files_blueprint()
    {
        var store = new YamlTemplateStore();
        var templates = store.LoadEmbeddedCatalog().ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        // Pin the per-template expansion counts -- these are the contract
        // operators see in the dashboard "this template will create N
        // rules" preview.
        Assert.Equal(3, templates["protect-login"].Expansion.Count);
        Assert.Equal(2, templates["protect-checkout"].Expansion.Count);
        Assert.Equal(3, templates["protect-search"].Expansion.Count);
        Assert.Equal(3, templates["protect-admin"].Expansion.Count);
        Assert.Single(templates["protect-expensive-endpoints"].Expansion);
        Assert.Equal(3, templates["protect-content-from-scraping"].Expansion.Count);
        Assert.Single(templates["keep-verified-bots-working"].Expansion);
        Assert.Single(templates["preserve-humans-during-overload"].Expansion);
        Assert.Equal(2, templates["aspnet-adaptive-rate-limit"].Expansion.Count);
        // B5: org.lockdown toggle template emits three rules (one per
        // actor class -- bot UA, bot type, VPN/Tor).
        Assert.Equal(3, templates["lockdown-mode"].Expansion.Count);
    }

    [Fact]
    public void Customer_directory_parses_alongside_the_embedded_catalog()
    {
        var tmp = Directory.CreateTempSubdirectory("template-store-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "customer.yaml"),
                """
                template: customer-block-all
                display-name: "Customer block all"
                description: "test"
                category: customer
                expansion:
                  - suffix: block
                    predicate: "ua.is_bot = true"
                    action: "{ kind: block }"
                    priority: 10
                    mode: live
                """);

            var store = new YamlTemplateStore();
            var all = store.LoadAll(tmp);

            Assert.Contains(all, t => string.Equals(t.Id, "customer-block-all", StringComparison.OrdinalIgnoreCase));
            // FOSS catalog still present.
            Assert.Contains(all, t => string.Equals(t.Id, "protect-login", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Customer_template_with_same_id_replaces_foss_entry()
    {
        var tmp = Directory.CreateTempSubdirectory("template-store-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "override.yaml"),
                """
                template: protect-login
                display-name: "Customer protect-login"
                description: "overridden"
                category: customer
                expansion:
                  - suffix: only
                    predicate: "ua.is_bot = true"
                    action: "{ kind: block }"
                    priority: 1
                    mode: live
                """);

            var store = new YamlTemplateStore();
            var all = store.LoadAll(tmp);

            var login = all.Single(t => string.Equals(t.Id, "protect-login", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Customer protect-login", login.DisplayName);
            Assert.Single(login.Expansion);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Malformed_customer_file_is_skipped_without_blocking_load()
    {
        var tmp = Directory.CreateTempSubdirectory("template-store-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "bad.yaml"), "::: not yaml :::");
            File.WriteAllText(Path.Combine(tmp, "good.yaml"),
                """
                template: customer-good
                display-name: "good"
                description: ""
                category: customer
                expansion:
                  - suffix: x
                    predicate: "ua.is_bot = true"
                    action: "{ kind: block }"
                    priority: 1
                    mode: live
                """);

            var store = new YamlTemplateStore();
            var fromDir = store.LoadFromDirectory(tmp);

            Assert.Single(fromDir);
            Assert.Equal("customer-good", fromDir[0].Id);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Null_or_missing_customer_directory_returns_empty_list()
    {
        var store = new YamlTemplateStore();

        Assert.Empty(store.LoadFromDirectory(null));
        Assert.Empty(store.LoadFromDirectory(""));
        Assert.Empty(store.LoadFromDirectory("/nonexistent/path/that/does/not/exist"));
    }
}
