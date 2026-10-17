using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
/// Gives atoms the request explicitly supplied to the orchestrator, including
/// synthetic API requests, while preserving the host's ambient accessor.
/// </summary>
internal sealed class DetectionHttpContextAccessor(DetectionHttpContextAccessor.HostAccessor host) : IHttpContextAccessor
{
    private static readonly AsyncLocal<Frame?> Current = new();

    public HttpContext? HttpContext
    {
        get => Current.Value?.Context ?? host.Value.HttpContext;
        set => host.Value.HttpContext = value;
    }

    internal static IDisposable Enter(HttpContext context)
    {
        var frame = new Frame(context, Current.Value);
        Current.Value = frame;
        return frame;
    }

    internal static void Register(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(HostAccessor))) return;
        services.AddHttpContextAccessor();
        var original = services.Last(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IHttpContextAccessor));
        services.Add(new ServiceDescriptor(typeof(HostAccessor), provider => new HostAccessor(
            (IHttpContextAccessor)(original.ImplementationInstance
                ?? original.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!)),
            original.ImplementationInstance is null), original.Lifetime));
        services.Add(new ServiceDescriptor(typeof(DetectionHttpContextAccessor),
            typeof(DetectionHttpContextAccessor), original.Lifetime));
        services[services.IndexOf(original)] = new ServiceDescriptor(typeof(IHttpContextAccessor),
            provider => provider.GetRequiredService<DetectionHttpContextAccessor>(), original.Lifetime);
    }

    private sealed class Frame(HttpContext context, Frame? previous) : IDisposable
    {
        public HttpContext? Context = context;
        public void Dispose()
        {
            if (Context is null) return;
            // Clear child async flows too, so detached work cannot retain a
            // synthetic request after detection has finished.
            Context = null;
            Current.Value = previous;
        }
    }

    internal sealed class HostAccessor(IHttpContextAccessor value, bool owned) : IDisposable, IAsyncDisposable
    {
        public IHttpContextAccessor Value { get; } = value;
        public void Dispose()
        {
            if (owned && Value is IDisposable disposable) disposable.Dispose();
        }
        public async ValueTask DisposeAsync()
        {
            if (!owned) return;
            if (Value is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (Value is IDisposable disposable) disposable.Dispose();
        }
    }
}
