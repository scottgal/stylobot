using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Mostlylucid.BotDetection.Console.Services;

public sealed record RemoteStats(
    int TotalRequests,
    int BotRequests,
    int HumanRequests,
    int UniqueSignatures,
    IReadOnlyDictionary<string, int> RiskBandCounts)
{
    public static readonly RemoteStats Empty =
        new(0, 0, 0, 0, new Dictionary<string, int>());
}

public sealed record RemoteBotEntry(
    string Signature,
    int HitCount,
    string? BotName,
    double BotProbability);

public sealed record RemoteDetectionEntry(
    DateTime Timestamp,
    string Path,
    double BotProbability,
    string? Action,
    string? Country);

public sealed record RemoteDashboardSnapshot(
    RemoteStats Stats,
    IReadOnlyList<RemoteBotEntry> TopBots,
    IReadOnlyList<RemoteDetectionEntry> RecentDetections,
    string ConnectionStatus);

public sealed class RemoteDashboardService : IAsyncDisposable
{
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly Channel<RemoteDashboardSnapshot> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;
    private Task? _runTask;

    public ChannelReader<RemoteDashboardSnapshot> Updates => _channel.Reader;
    public string ConnectionStatus { get; private set; } = "Initializing...";

    public RemoteDashboardService(string baseUrl, string? apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _channel = Channel.CreateBounded<RemoteDashboardSnapshot>(
            new BoundedChannelOptions(5) { FullMode = BoundedChannelFullMode.DropOldest });
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (apiKey != null)
            _http.DefaultRequestHeaders.Add("X-SB-Api-Key", apiKey);
    }

    public void Start() => _runTask = RunAsync(_cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        await PollAndPublishAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                ConnectionStatus = "Reconnecting...";
                _ = ex; // network failures are expected; retry after delay
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        var wsUrl = _baseUrl
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            + "/stylobot/hub";

        using var ws = new ClientWebSocket();
        if (_apiKey != null)
            ws.Options.SetRequestHeader("X-SB-Api-Key", _apiKey);

        await ws.ConnectAsync(new Uri(wsUrl), ct);

        // SignalR JSON hub protocol handshake
        await ws.SendAsync(
            "{\"protocol\":\"json\",\"version\":1}\u001e"u8.ToArray(),
            WebSocketMessageType.Text, true, ct);

        ConnectionStatus = "Connected";

        var buffer = new byte[8192];
        var accumulated = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                ConnectionStatus = "Disconnected";
                return;
            }

            accumulated.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (!result.EndOfMessage) continue;

            var raw = accumulated.ToString();
            accumulated.Clear();

            // SignalR frames are delimited by record separator (0x1E)
            foreach (var frame in raw.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                await HandleFrameAsync(frame, ct);
        }
    }

    private async Task HandleFrameAsync(string frame, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            // Type 1 = invocation (server calling client method)
            if (!root.TryGetProperty("type", out var t) || t.GetInt32() != 1) return;
            if (!root.TryGetProperty("target", out _)) return;
            await PollAndPublishAsync(ct);
        }
        catch (JsonException) { }
    }

    private async Task PollAndPublishAsync(CancellationToken ct)
    {
        try
        {
            using var statsDoc   = await FetchJsonAsync("/api/summary", ct);
            using var botsDoc    = await FetchJsonAsync("/api/topbots?pageSize=8", ct);
            using var detectDoc  = await FetchJsonAsync("/api/detections?limit=50", ct);

            _channel.Writer.TryWrite(new RemoteDashboardSnapshot(
                ParseStats(statsDoc),
                ParseTopBots(botsDoc),
                ParseDetections(detectDoc),
                ConnectionStatus));
        }
        catch (OperationCanceledException) { }
        catch { /* best-effort */ }
    }

    private async Task<JsonDocument> FetchJsonAsync(string path, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(_baseUrl + path, ct);
        return JsonDocument.Parse(json);
    }

    private static RemoteStats ParseStats(JsonDocument doc)
    {
        var r = doc.RootElement;
        var bands = new Dictionary<string, int>();
        if (r.TryGetProperty("riskBandCounts", out var b))
            foreach (var kv in b.EnumerateObject())
                bands[kv.Name] = kv.Value.GetInt32();

        return new RemoteStats(
            r.TryGetProperty("totalRequests",    out var tr) ? tr.GetInt32() : 0,
            r.TryGetProperty("botRequests",      out var br) ? br.GetInt32() : 0,
            r.TryGetProperty("humanRequests",    out var hr) ? hr.GetInt32() : 0,
            r.TryGetProperty("uniqueSignatures", out var us) ? us.GetInt32() : 0,
            bands);
    }

    private static IReadOnlyList<RemoteBotEntry> ParseTopBots(JsonDocument doc)
    {
        var list = new List<RemoteBotEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
            list.Add(new RemoteBotEntry(
                item.TryGetProperty("primarySignature", out var s)  ? s.GetString() ?? "" : "",
                item.TryGetProperty("hitCount",         out var hc) ? hc.GetInt32()       : 0,
                item.TryGetProperty("botName",          out var bn) ? bn.GetString()       : null,
                item.TryGetProperty("botProbability",   out var bp) ? bp.GetDouble()       : 0));
        return list.AsReadOnly();
    }

    private static IReadOnlyList<RemoteDetectionEntry> ParseDetections(JsonDocument doc)
    {
        var list = new List<RemoteDetectionEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
            list.Add(new RemoteDetectionEntry(
                item.TryGetProperty("timestamp",      out var ts) &&
                DateTime.TryParse(ts.GetString(), out var dt) ? dt : DateTime.Now,
                item.TryGetProperty("path",           out var p)  ? p.GetString()  ?? "/" : "/",
                item.TryGetProperty("botProbability", out var bp) ? bp.GetDouble()         : 0,
                item.TryGetProperty("action",         out var a)  ? a.GetString()          : null,
                item.TryGetProperty("countryCode",    out var cc) ? cc.GetString()         : null));
        return list.AsReadOnly();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_runTask != null)
            try { await _runTask; } catch { }
        _cts.Dispose();
        _http.Dispose();
    }
}
