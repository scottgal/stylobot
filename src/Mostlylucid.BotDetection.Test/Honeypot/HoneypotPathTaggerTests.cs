using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Honeypot;

namespace Mostlylucid.BotDetection.Test.Honeypot;

public class HoneypotPathTaggerTests
{
    private static HoneypotPathTagger CreateTagger(
        HoneypotDetectionOptions? options = null,
        RequestDelegate? next = null)
    {
        options ??= new HoneypotDetectionOptions();
        var monitor = new TestOptionsMonitor<HoneypotDetectionOptions>(options);
        var exemptStore = new ConfigHoneypotExemptStore(monitor);
        return new HoneypotPathTagger(next ?? (_ => Task.CompletedTask), monitor, exemptStore);
    }

    [Fact]
    public async Task Tier1_AwsCredentials_TagsAsAlways()
    {
        var tagger = CreateTagger();
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/.aws/credentials";

        await tagger.InvokeAsync(ctx);

        Assert.Equal(HoneypotTier.Always, ctx.Items[HoneypotPathTagger.ItemKeyTier]);
        Assert.Equal("/.aws/credentials", ctx.Items[HoneypotPathTagger.ItemKeyNormalizedPath]);
        Assert.True(ctx.Items[HoneypotPathTagger.LegacyItemKeyIsHoneypotPath] is true);
    }

    [Fact]
    public async Task Tier1_EnvLocalSave_TagsAsAlways()
    {
        var tagger = CreateTagger();
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/.env.local.save";

        await tagger.InvokeAsync(ctx);

        Assert.Equal(HoneypotTier.Always, ctx.Items[HoneypotPathTagger.ItemKeyTier]);
        Assert.Equal("/.env*", ctx.Items[HoneypotPathTagger.ItemKeyMatchedPattern]);
    }

    [Fact]
    public async Task Tier2_WpLogin_TagsAsProbable()
    {
        var tagger = CreateTagger();
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/wp-login.php";

        await tagger.InvokeAsync(ctx);

        Assert.Equal(HoneypotTier.Probable, ctx.Items[HoneypotPathTagger.ItemKeyTier]);
    }

    [Fact]
    public async Task Tier2_ExemptPath_NoTagWritten()
    {
        var tagger = CreateTagger(new HoneypotDetectionOptions
        {
            ExemptPaths = ["/wp-login.php"]
        });
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/wp-login.php";

        await tagger.InvokeAsync(ctx);

        Assert.False(ctx.Items.ContainsKey(HoneypotPathTagger.ItemKeyTier));
    }

    [Fact]
    public async Task Tier1_ExemptPath_StillTags_NotExemptable()
    {
        // Tier 1 paths are NEVER exempt-able even when listed.
        var tagger = CreateTagger(new HoneypotDetectionOptions
        {
            ExemptPaths = ["/.aws/credentials"]
        });
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/.aws/credentials";

        await tagger.InvokeAsync(ctx);

        Assert.Equal(HoneypotTier.Always, ctx.Items[HoneypotPathTagger.ItemKeyTier]);
    }

    [Fact]
    public async Task AdditionalPaths_OperatorSupplied_TagsAsProbable()
    {
        var tagger = CreateTagger(new HoneypotDetectionOptions
        {
            AdditionalPaths = ["/api/v2/internal-debug"]
        });
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/v2/internal-debug";

        await tagger.InvokeAsync(ctx);

        Assert.Equal(HoneypotTier.Probable, ctx.Items[HoneypotPathTagger.ItemKeyTier]);
    }

    [Fact]
    public async Task Disabled_NoTagWritten()
    {
        var tagger = CreateTagger(new HoneypotDetectionOptions { Enabled = false });
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/.aws/credentials";

        await tagger.InvokeAsync(ctx);

        Assert.False(ctx.Items.ContainsKey(HoneypotPathTagger.ItemKeyTier));
    }

    [Fact]
    public async Task NormalPath_NoTagWritten()
    {
        var tagger = CreateTagger();
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/products/123";

        await tagger.InvokeAsync(ctx);

        Assert.False(ctx.Items.ContainsKey(HoneypotPathTagger.ItemKeyTier));
    }

    [Fact]
    public async Task CallsNext()
    {
        var called = false;
        var tagger = CreateTagger(next: _ => { called = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/anything";

        await tagger.InvokeAsync(ctx);

        Assert.True(called);
    }

    [Fact]
    public async Task UpperCasePath_StillMatches()
    {
        var tagger = CreateTagger();
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/.AWS/CREDENTIALS";

        await tagger.InvokeAsync(ctx);

        Assert.Equal(HoneypotTier.Always, ctx.Items[HoneypotPathTagger.ItemKeyTier]);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
