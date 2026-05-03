using Mostlylucid.BotDetection.Llm.Holodeck;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class HolodeckPromptBuilderTests
{
    [Fact]
    public void Build_IncludesFrameworkAndVersion()
    {
        var template = CreateTemplate(hints: new PackResponseHints { EndpointDescription = "WordPress login page", ResponseFormat = "html" });
        var context = CreateContext(framework: "WordPress", version: "5.9");
        var prompt = HolodeckPromptBuilder.Build(template, context, canary: null);
        Assert.Contains("WordPress", prompt);
        Assert.Contains("5.9", prompt);
        Assert.Contains("login page", prompt);
        Assert.Contains("html", prompt);
    }

    [Fact]
    public void Build_IncludesCanaryInstructions()
    {
        var prompt = HolodeckPromptBuilder.Build(CreateTemplate(), CreateContext(), canary: "abc12345");
        Assert.Contains("abc12345", prompt);
        Assert.Contains("nonce", prompt.ToLowerInvariant());
    }

    [Fact]
    public void Build_OmitsCanaryWhenNull()
    {
        var prompt = HolodeckPromptBuilder.Build(CreateTemplate(), CreateContext(), canary: null);
        Assert.DoesNotContain("Embed this exact value", prompt);
    }

    [Fact]
    public void Build_IncludesBodySchema()
    {
        var template = CreateTemplate(hints: new PackResponseHints { BodySchema = "{\"users\": [{\"id\": 1}]}", ResponseFormat = "json" });
        var prompt = HolodeckPromptBuilder.Build(template, CreateContext(), canary: null);
        Assert.Contains("{\"users\"", prompt);
    }

    [Fact]
    public void Build_IncludesPackPersonality()
    {
        var context = CreateContext() with { PackPersonality = "Respond as a misconfigured Apache server with debug mode enabled" };
        var prompt = HolodeckPromptBuilder.Build(CreateTemplate(), context, canary: null);
        Assert.Contains("misconfigured Apache", prompt);
    }

    [Fact]
    public void Build_IncludesMethodAndPath()
    {
        var prompt = HolodeckPromptBuilder.Build(CreateTemplate(), CreateContext(method: "POST", path: "/wp-login.php"), canary: null);
        Assert.Contains("POST", prompt);
        Assert.Contains("/wp-login.php", prompt);
    }

    private static PackResponseTemplate CreateTemplate(PackResponseHints? hints = null) =>
        new() { PathPattern = "*", Body = "fallback", Dynamic = true, ContentType = "text/html",
            ResponseHints = hints ?? new PackResponseHints { EndpointDescription = "Test endpoint", ResponseFormat = "html" } };

    private static HolodeckRequestContext CreateContext(string method = "GET", string path = "/test", string framework = "WordPress", string version = "5.9") =>
        new() { Method = method, Path = path, PackFramework = framework, PackVersion = version };
}
