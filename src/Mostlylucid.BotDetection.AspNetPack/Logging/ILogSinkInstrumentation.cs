using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.AspNetPack.Logging;

public interface ILogSinkInstrumentation
{
    void RecordEmitted(LogLevel level);
    void RecordShipped(string result, long count);
    void RecordGatewayUnreachable();
    void SetQueueDepth(long n);
}

internal sealed class NoOpLogSinkInstrumentation : ILogSinkInstrumentation
{
    public static readonly NoOpLogSinkInstrumentation Instance = new();
    public void RecordEmitted(LogLevel level) { }
    public void RecordShipped(string result, long count) { }
    public void RecordGatewayUnreachable() { }
    public void SetQueueDepth(long n) { }
}
