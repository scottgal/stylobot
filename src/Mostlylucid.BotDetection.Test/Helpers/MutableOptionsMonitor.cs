using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Test.Helpers;

/// <summary>
///     Test double for <see cref="IOptionsMonitor{TOptions}"/> with a settable
///     <see cref="CurrentValue"/>, so tests can simulate a live config change
///     (e.g. an operator flipping a stabiliser flag via <c>/admin/reload</c>)
///     without needing a real <see cref="IConfiguration"/> + change-token pipeline.
///     Also works as a drop-in for the common "just wrap a static value" case
///     that <c>Microsoft.Extensions.Options.Options.Create</c> covers for
///     <see cref="IOptions{TOptions}"/> but has no equivalent for the monitor
///     interface.
/// </summary>
public sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
