using Mostlylucid.BotDetection.Orchestration.Telemetry;

namespace Mostlylucid.BotDetection.Observability.Events;

internal static class DetectionEventLogProperties
{
    public static object[] ToLogArgs(this DetectionEvent evt) => new object[]
    {
        evt.Signature,
        evt.IsBot,
        evt.BotProbability,
        evt.Confidence,
        evt.RiskBand ?? "unknown",
        evt.ThreatBand ?? "unknown",
        evt.Action ?? "none",
        evt.BotName ?? string.Empty,
        evt.BotType ?? string.Empty,
        evt.CountryCode ?? string.Empty,
        evt.Path ?? string.Empty,
        evt.Method ?? string.Empty,
        evt.StatusCode,
        evt.ProcessingTimeMs,
        evt.RequestId,
        evt.GatewayId ?? string.Empty
    };

    public const string MessageTemplate =
        "StyloBot detection: signature={StyloBot_Signature} isBot={StyloBot_IsBot} " +
        "prob={StyloBot_Probability} conf={StyloBot_Confidence} " +
        "risk={StyloBot_RiskBand} threat={StyloBot_ThreatBand} action={StyloBot_Action} " +
        "botName={StyloBot_BotName} botType={StyloBot_BotType} country={StyloBot_CountryCode} " +
        "path={StyloBot_Path} method={StyloBot_Method} status={StyloBot_StatusCode} " +
        "elapsedMs={StyloBot_ProcessingTimeMs} requestId={StyloBot_RequestId} gateway={StyloBot_GatewayId}";
}
