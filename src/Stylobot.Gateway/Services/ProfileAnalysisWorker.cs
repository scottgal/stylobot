using System.Net;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Data;

namespace Stylobot.Gateway.Services;

public class ProfileAnalysisWorker(
    IServiceScopeFactory scopeFactory,
    ProfileAnalysisChannel channel,
    ProfileCalibrationStore store,
    IOptions<ProfileModeOptions> options,
    IOptions<BotDetectionOptions> detectionOptions,
    ILogger<ProfileAnalysisWorker> logger) : BackgroundService
{
    private const string PolicyName = "yarp-learning";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;

        using var sem = new SemaphoreSlim(options.Value.Concurrency, options.Value.Concurrency);

        await foreach (var snapshot in channel.ReadAllAsync(stoppingToken))
        {
            await sem.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try { await ProcessSnapshotAsync(snapshot, stoppingToken); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Profile analysis failed for {RequestId}", snapshot.RequestId);
                }
                finally { sem.Release(); }
            }, stoppingToken);
        }
    }

    private async Task ProcessSnapshotAsync(ProfileRequestSnapshot snapshot, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();

        var ctx = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        ctx.Request.Method = snapshot.Method;
        ctx.Request.Path = new PathString(snapshot.Path);
        ctx.Connection.RemoteIpAddress =
            IPAddress.TryParse(snapshot.ClientIp, out var ip) ? ip : null;

        foreach (var (key, values) in snapshot.Headers)
            ctx.Request.Headers[key] = values;

        if (snapshot.TlsProtocol != null) ctx.Items["TLS.Protocol"] = snapshot.TlsProtocol;
        if (snapshot.TlsCipherSuite != null) ctx.Items["TLS.CipherSuite"] = snapshot.TlsCipherSuite;

        var evidence = await RunDetectionAsync(ctx, scope.ServiceProvider, ct);

        var probability = evidence?.BotProbability ?? 0.0;
        var botThreshold = detectionOptions.Value.BotThreshold;
        var isBot = probability >= botThreshold;
        var botType = evidence?.PrimaryBotType?.ToString();
        var botName = evidence?.PrimaryBotName;

        var riskBand = probability switch
        {
            < 0.3 => "Low",
            < 0.5 => "Medium",
            < 0.7 => "High",
            _ => "VeryHigh",
        };

        await store.InsertAsync(new ProfileCalibrationEntry
        {
            SignatureHash = snapshot.RequestId,
            BotProbability = probability,
            RiskBand = riskBand,
            BotType = isBot ? botType : null,
            BotName = botName,
            TopDetector = null,
            PathPattern = NormalizePath(snapshot.Path),
        }, ct);
    }

    private async Task<AggregatedEvidence?> RunDetectionAsync(
        HttpContext ctx, IServiceProvider services, CancellationToken ct)
    {
        var orchestrator = services.GetService<BlackboardOrchestrator>();
        if (orchestrator is null)
        {
            logger.LogWarning("BlackboardOrchestrator not available in profile analysis scope");
            return null;
        }

        return await orchestrator.DetectAsync(ctx, PolicyName, ct);
    }

    private static string NormalizePath(string path)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(
            path,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            "{id}");
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized, @"/\d+(/|$)", "/{id}$1");
        return normalized;
    }
}
