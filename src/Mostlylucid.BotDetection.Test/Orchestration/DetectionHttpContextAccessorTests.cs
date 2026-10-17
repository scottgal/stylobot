using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public sealed class DetectionHttpContextAccessorTests
{
    [Fact]
    public async Task Keyed_host_accessors_are_preserved()
    {
        var host = new TestAccessor { HttpContext = new DefaultHttpContext() };
        var keyed = new TestAccessor();
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(host);
        services.AddKeyedSingleton<IHttpContextAccessor>("other", keyed);
        DetectionHttpContextAccessor.Register(services);
        await using var provider = services.BuildServiceProvider();
        Assert.Same(host.HttpContext, provider.GetRequiredService<IHttpContextAccessor>().HttpContext);
        Assert.Same(keyed, provider.GetRequiredKeyedService<IHttpContextAccessor>("other"));
    }

    [Fact]
    public async Task Synthetic_context_is_restored_without_mutating_the_hosts_accessor()
    {
        var original = new TestAccessor { HttpContext = new DefaultHttpContext() };
        var host = original.HttpContext;
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(original);
        DetectionHttpContextAccessor.Register(services);
        DetectionHttpContextAccessor.Register(services);
        await using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var synthetic = new DefaultHttpContext();
        using (DetectionHttpContextAccessor.Enter(synthetic))
        {
            await Task.Yield();
            Assert.Same(synthetic, accessor.HttpContext);
            Assert.Same(host, original.HttpContext);
            using (DetectionHttpContextAccessor.Enter(new DefaultHttpContext())) { }
            Assert.Same(synthetic, accessor.HttpContext);
        }
        Assert.Same(host, accessor.HttpContext);
    }

    [Fact]
    public async Task Concurrent_synthetic_requests_do_not_share_context()
    {
        var services = new ServiceCollection();
        DetectionHttpContextAccessor.Register(services);
        await using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task Detect(HttpContext context)
        {
            using var scope = DetectionHttpContextAccessor.Enter(context);
            if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Same(context, accessor.HttpContext);
        }
        await Task.WhenAll(Detect(new DefaultHttpContext()), Detect(new DefaultHttpContext()));
    }

    [Fact]
    public async Task Disposing_detection_clears_context_from_detached_child_flows()
    {
        var original = new TestAccessor { HttpContext = new DefaultHttpContext() };
        var accessor = new DetectionHttpContextAccessor(new DetectionHttpContextAccessor.HostAccessor(original, false));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpContext?> child;
        using (DetectionHttpContextAccessor.Enter(new DefaultHttpContext()))
            child = Task.Run(async () => { await release.Task; return accessor.HttpContext; });
        release.TrySetResult();
        Assert.Same(original.HttpContext, await child.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class TestAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
