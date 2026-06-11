using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.Test.Policies.Templates;

/// <summary>
///     T5 YAML round-trip tests: confirm <c>extends:</c> parses through the
///     <see cref="YamlTemplateStore"/> and is applied by the
///     <see cref="TemplateRegistry"/>.
/// </summary>
public class YamlTemplateStoreExtendsTests
{
    [Fact]
    public void Yaml_with_extends_parses_into_typed_record()
    {
        var tmp = Directory.CreateTempSubdirectory("template-extends-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "child.yaml"),
                """
                template: my-org-protect-login
                extends: protect-login
                display-name: "My-org protect-login"
                description: "extends FOSS"
                category: customer
                parameters:
                  - name: human-rpm
                    type: int
                    default: 4
                """);

            var store = new YamlTemplateStore();
            var templates = store.LoadFromDirectory(tmp);

            var child = Assert.Single(templates);
            Assert.Equal("my-org-protect-login", child.Id);
            Assert.Equal("protect-login", child.ExtendsTemplateId);
            Assert.Empty(child.Expansion);
            Assert.Single(child.Parameters);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Yaml_without_extends_parses_as_today_back_compat()
    {
        var tmp = Directory.CreateTempSubdirectory("template-extends-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "plain.yaml"),
                """
                template: my-plain
                display-name: "Plain"
                description: "no extends"
                category: customer
                expansion:
                  - suffix: only
                    predicate: "ua.is_bot = true"
                    action: "{ kind: block }"
                    priority: 10
                    mode: live
                """);

            var store = new YamlTemplateStore();
            var templates = store.LoadFromDirectory(tmp);

            var t = Assert.Single(templates);
            Assert.Null(t.ExtendsTemplateId);
            Assert.Single(t.Expansion);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void End_to_end_parent_and_child_yaml_registry_returns_resolved_child()
    {
        var tmp = Directory.CreateTempSubdirectory("template-extends-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "parent.yaml"),
                """
                template: parent-template
                display-name: "Parent"
                description: "base"
                category: test
                parameters:
                  - name: rpm
                    type: int
                    default: 6
                expansion:
                  - suffix: throttle
                    predicate: "ua.is_bot = false"
                    action: "{ kind: rate_limit, rpm: {{ rpm }} }"
                    priority: 100
                    mode: live
                """);

            File.WriteAllText(Path.Combine(tmp, "child.yaml"),
                """
                template: child-template
                extends: parent-template
                display-name: "Child"
                description: "extends parent"
                category: test
                parameters:
                  - name: rpm
                    type: int
                    default: 12
                expansion:
                  - suffix: block-bad
                    predicate: "ua.is_bot = true"
                    action: "{ kind: block }"
                    priority: 50
                    mode: live
                """);

            var store = new YamlTemplateStore();
            var templates = store.LoadFromDirectory(tmp);
            Assert.Equal(2, templates.Count);

            var registry = new TemplateRegistry(templates);
            var resolved = registry.Find("child-template")!;

            Assert.NotNull(resolved);
            Assert.Null(resolved.ExtendsTemplateId);
            // Parent's expansion + child's expansion.
            Assert.Equal(2, resolved.Expansion.Count);
            Assert.Equal("throttle", resolved.Expansion[0].Suffix);
            Assert.Equal("block-bad", resolved.Expansion[1].Suffix);
            // Child override wins on parameter default.
            var rpm = resolved.Parameters.Single();
            Assert.Equal("rpm", rpm.Name);
            Assert.Equal(12, rpm.Default);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
