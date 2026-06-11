using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.Test.Policies.Templates;

/// <summary>
///     Parser-level tests for <see cref="YamlTemplateStore.LoadApplicationsFromDirectory"/>.
///     Pins the YAML application file shape from the T2 spec: top-level
///     <c>application</c> id, <c>template</c> reference, optional
///     <c>applied-to</c> list of composite scopes, optional
///     <c>parameters</c> map.
/// </summary>
public class YamlTemplateStoreApplicationsTests
{
    private const string SampleApplicationYaml = """
        application: my-login-protection
        template: protect-login
        applied-to:
          - { host: { kind: subdomain, domain: example.com, sub: api }, method: POST, host-path: /login }
        parameters:
          human-rpm: 10
          credstuff-threshold: 0.7
        """;

    [Fact]
    public void Parses_sample_application_yaml_into_typed_record()
    {
        var tmp = Directory.CreateTempSubdirectory("template-app-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "app.yaml"), SampleApplicationYaml);

            var store = new YamlTemplateStore();
            var apps = store.LoadApplicationsFromDirectory(tmp);

            var app = Assert.Single(apps);
            Assert.Equal("my-login-protection", app.Id);
            Assert.Equal("protect-login", app.TemplateId);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Application_with_all_fields_present_round_trips_scope_and_parameters()
    {
        var tmp = Directory.CreateTempSubdirectory("template-app-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "app.yaml"), SampleApplicationYaml);

            var store = new YamlTemplateStore();
            var app = store.LoadApplicationsFromDirectory(tmp).Single();

            // Scope -- subdomain + host-path becomes endpoint scope.
            var scope = Assert.Single(app.AppliedTo);
            var endpoint = Assert.IsType<HostScope.Endpoint>(scope.Host);
            Assert.Equal("example.com", endpoint.DomainName);
            Assert.Equal("api", endpoint.SubdomainName);
            Assert.Equal("/login", endpoint.PathTemplate);
            Assert.Equal("POST", scope.Method);

            // Parameters are case-insensitive and carry the typed values that
            // VYaml read as YAML scalars.
            Assert.True(app.Parameters.ContainsKey("human-rpm"));
            Assert.True(app.Parameters.ContainsKey("HUMAN-RPM"));
            Assert.True(app.Parameters.ContainsKey("credstuff-threshold"));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Application_referencing_unknown_template_still_parses_validation_is_materializers_job()
    {
        var tmp = Directory.CreateTempSubdirectory("template-app-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "app.yaml"),
                """
                application: orphan
                template: never-defined-anywhere
                applied-to: []
                parameters: {}
                """);

            var store = new YamlTemplateStore();
            var apps = store.LoadApplicationsFromDirectory(tmp);

            var app = Assert.Single(apps);
            Assert.Equal("orphan", app.Id);
            Assert.Equal("never-defined-anywhere", app.TemplateId);
            Assert.Empty(app.AppliedTo);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Null_or_missing_application_directory_returns_empty_list()
    {
        var store = new YamlTemplateStore();

        Assert.Empty(store.LoadApplicationsFromDirectory(null));
        Assert.Empty(store.LoadApplicationsFromDirectory(""));
        Assert.Empty(store.LoadApplicationsFromDirectory("/nonexistent/path/that/does/not/exist"));
    }

    [Fact]
    public void Malformed_application_file_is_skipped_without_blocking_other_files()
    {
        var tmp = Directory.CreateTempSubdirectory("template-app-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "bad.yaml"), "::: not yaml :::");
            File.WriteAllText(Path.Combine(tmp, "good.yaml"),
                """
                application: ok
                template: t
                applied-to: []
                parameters: {}
                """);

            var store = new YamlTemplateStore();
            var apps = store.LoadApplicationsFromDirectory(tmp);

            var app = Assert.Single(apps);
            Assert.Equal("ok", app.Id);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
