using Fluid;
using Grpc.Core;
using Mostlylucid.BotDetection.Api.Bridge;
using Mostlylucid.BotDetection.Orchestration;
using ApiModels = Mostlylucid.BotDetection.Api.Models;
using Proto = Stylobot.Detection.V1;

namespace Mostlylucid.BotDetection.Sidecar.Services;

public sealed class DetectionGrpcService : Proto.DetectionService.DetectionServiceBase
{
    private readonly BlackboardOrchestrator _orchestrator;
    private readonly SidecarOptions _options;
    private readonly TemplateOptions _templateOptions;
    private static readonly FluidParser Parser = new();

    public DetectionGrpcService(BlackboardOrchestrator orchestrator, SidecarOptions options)
    {
        _orchestrator = orchestrator;
        _options = options;
        // Bound execution for caller-supplied Liquid templates: MaxSteps caps loop
        // iterations (and therefore CPU and output size) so a hostile template cannot
        // turn RenderWidget into a denial-of-service vector.
        _templateOptions = new TemplateOptions { MaxSteps = options.MaxRenderSteps };
    }

    public override async Task<Proto.DetectResponse> Detect(Proto.DetectRequest request, ServerCallContext context)
    {
        var httpContext = BuildHttpContext(request);
        var evidence = await _orchestrator.DetectAsync(httpContext, context.CancellationToken);
        return ToResponse(evidence);
    }

    public override async Task<Proto.DetectBatchResponse> DetectBatch(Proto.DetectBatchRequest request, ServerCallContext context)
    {
        if (request.Requests.Count > _options.MaxBatchSize)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"batch too large: {request.Requests.Count} requests, maximum is {_options.MaxBatchSize}"));

        var batch = new Proto.DetectBatchResponse();
        foreach (var req in request.Requests)
        {
            var httpContext = BuildHttpContext(req);
            var evidence = await _orchestrator.DetectAsync(httpContext, context.CancellationToken);
            batch.Responses.Add(ToResponse(evidence));
        }
        return batch;
    }

    public override async Task<Proto.RenderWidgetResponse> RenderWidget(Proto.RenderWidgetRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.Template))
            return new Proto.RenderWidgetResponse { Success = false, Error = "template is required" };

        if (request.Template.Length > _options.MaxTemplateLength)
            return new Proto.RenderWidgetResponse
            {
                Success = false,
                Error = $"template too large: {request.Template.Length} characters, " +
                        $"maximum is {_options.MaxTemplateLength}",
            };

        if (!Parser.TryParse(request.Template, out var template, out var error))
            return new Proto.RenderWidgetResponse { Success = false, Error = error };

        var templateContext = new TemplateContext(_templateOptions);

        if (request.Verdict is { } v)
        {
            templateContext.SetValue("isBot", v.IsBot);
            templateContext.SetValue("botProbability", (double)v.BotProbability);
            templateContext.SetValue("confidence", (double)v.Confidence);
            templateContext.SetValue("botType", v.BotType);
            templateContext.SetValue("botName", v.BotName);
            templateContext.SetValue("riskBand", v.RiskBand.ToString());
            templateContext.SetValue("recommendedAction", v.RecommendedAction.ToString());
            templateContext.SetValue("threatScore", (double)v.ThreatScore);
            templateContext.SetValue("threatBand", v.ThreatBand.ToString());
        }

        foreach (var kv in request.Vars)
            templateContext.SetValue(kv.Key, kv.Value);

        try
        {
            var html = await template.RenderAsync(templateContext);
            return new Proto.RenderWidgetResponse { Html = html, Success = true };
        }
        catch (Exception ex)
        {
            // Render-time failure — most importantly the MaxSteps limit being hit.
            // Surface it as a failed render rather than an unhandled RPC fault.
            return new Proto.RenderWidgetResponse { Success = false, Error = $"render failed: {ex.Message}" };
        }
    }

    private static Microsoft.AspNetCore.Http.HttpContext BuildHttpContext(Proto.DetectRequest r) =>
        SyntheticHttpContext.FromDetectRequest(new ApiModels.DetectRequest
        {
            Method = r.Method,
            Path = r.Path,
            Headers = new Dictionary<string, string>(r.Headers),
            RemoteIp = r.RemoteIp,
            Protocol = string.IsNullOrEmpty(r.Protocol) ? "https" : r.Protocol,
            Tls = r.Tls is { } tls ? new ApiModels.TlsInfo
            {
                Version = tls.Version,
                Cipher = tls.Cipher,
                Ja3 = tls.Ja3,
                Ja4 = tls.Ja4,
            } : null,
        });

    private static Proto.DetectResponse ToResponse(AggregatedEvidence e)
    {
        var response = new Proto.DetectResponse
        {
            IsBot = e.BotProbability >= 0.7,
            BotProbability = (float)e.BotProbability,
            Confidence = (float)e.Confidence,
            BotType = e.PrimaryBotType?.ToString() ?? string.Empty,
            BotName = e.PrimaryBotName ?? string.Empty,
            RiskBand = MapRiskBand(e.RiskBand),
            RecommendedAction = MapAction(e.RiskBand),
            ThreatScore = (float)e.ThreatScore,
            ThreatBand = MapThreatBand(e.ThreatBand),
            ProcessingTimeMs = (float)e.TotalProcessingTimeMs,
            DetectorsRun = e.ContributingDetectors.Count,
        };

        foreach (var c in e.Contributions)
        {
            if (Math.Abs(c.ConfidenceDelta) > 0.01)
                response.Reasons.Add(new Proto.Reason
                {
                    Detector = c.DetectorName,
                    Detail = c.Reason ?? c.DetectorName,
                    Impact = (float)c.ConfidenceDelta,
                });
        }

        return response;
    }

    // Protobuf C# codegen strips the enum name prefix (e.g. RISK_BAND_ -> Proto.RiskBand.VeryLow)
    private static Proto.RiskBand MapRiskBand(RiskBand b) => b switch
    {
        RiskBand.VeryLow  => Proto.RiskBand.VeryLow,
        RiskBand.Low      => Proto.RiskBand.Low,
        RiskBand.Elevated => Proto.RiskBand.Elevated,
        RiskBand.Medium   => Proto.RiskBand.Medium,
        RiskBand.High     => Proto.RiskBand.High,
        RiskBand.VeryHigh => Proto.RiskBand.VeryHigh,
        RiskBand.Verified => Proto.RiskBand.Verified,
        _                 => Proto.RiskBand.Unknown,
    };

    private static Proto.RecommendedAction MapAction(RiskBand b) => b switch
    {
        RiskBand.High or RiskBand.VeryHigh => Proto.RecommendedAction.Block,
        RiskBand.Medium                    => Proto.RecommendedAction.Challenge,
        RiskBand.Elevated                  => Proto.RecommendedAction.Throttle,
        _                                  => Proto.RecommendedAction.Allow,
    };

    private static Proto.ThreatBand MapThreatBand(ThreatBand b) => b switch
    {
        ThreatBand.Low      => Proto.ThreatBand.Low,
        ThreatBand.Elevated => Proto.ThreatBand.Elevated,
        ThreatBand.High     => Proto.ThreatBand.High,
        ThreatBand.Critical => Proto.ThreatBand.Critical,
        _                   => Proto.ThreatBand.None,
    };
}
