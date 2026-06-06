using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Serilog.Core;
using Serilog.Events;

namespace Mostlylucid.BotDetection.Observability.Enrichment;

/// <summary>
///     Serilog enricher that reads the current request's StyloBot verdict from
///     <see cref="HttpContext.Items"/> and adds <c>StyloBot_*</c> properties to every
///     log line emitted during the request. Customer host controller logs then join
///     the bot-detection dataset in the observability backend.
/// </summary>
/// <remarks>
///     Only fields that the live detection pipeline stamps onto <c>HttpContext.Items</c>
///     are emitted; risk-band / threat-band aren't on Items today (they live on the
///     blackboard signals + on the proxied outbound headers), so they're intentionally
///     omitted to avoid silently emitting empty properties.
///     <para>
///         Key constants verified against:
///         <list type="bullet">
///             <item><c>BotDetectionMiddleware.IsBotKey</c> (BotDetectionMiddleware.cs:1125)</item>
///             <item><c>BotDetectionMiddleware.BotProbabilityKey</c> (BotDetectionMiddleware.cs:1131)</item>
///             <item><c>BotDetectionMiddleware.BotTypeKey</c> (BotDetectionMiddleware.cs:1137)</item>
///             <item><c>BotDetectionMiddleware.BotNameKey</c> (BotDetectionMiddleware.cs:1140)</item>
///             <item><c>BotDetectionMiddleware.PolicyNameKey</c> (BotDetectionMiddleware.cs:1149)</item>
///             <item><c>BotDetectionMiddleware.PolicyActionKey</c> (BotDetectionMiddleware.cs:1152)</item>
///             <item><c>SignalKeys.PrimarySignature</c> (DetectionContext.cs:478)</item>
///         </list>
///     </para>
/// </remarks>
public sealed class StyloBotLogEnricher : ILogEventEnricher
{
    private readonly IHttpContextAccessor _accessor;

    public StyloBotLogEnricher(IHttpContextAccessor accessor)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var ctx = _accessor.HttpContext;
        if (ctx is null) return;

        var items = ctx.Items;
        Add(logEvent, propertyFactory, "StyloBot_Signature",      items[SignalKeys.PrimarySignature]);
        Add(logEvent, propertyFactory, "StyloBot_IsBot",          items[BotDetectionMiddleware.IsBotKey]);
        Add(logEvent, propertyFactory, "StyloBot_BotProbability", items[BotDetectionMiddleware.BotProbabilityKey]);
        Add(logEvent, propertyFactory, "StyloBot_BotType",        items[BotDetectionMiddleware.BotTypeKey]);
        Add(logEvent, propertyFactory, "StyloBot_BotName",        items[BotDetectionMiddleware.BotNameKey]);
        Add(logEvent, propertyFactory, "StyloBot_PolicyName",     items[BotDetectionMiddleware.PolicyNameKey]);
        Add(logEvent, propertyFactory, "StyloBot_Action",         items[BotDetectionMiddleware.PolicyActionKey]);
    }

    private static void Add(LogEvent logEvent, ILogEventPropertyFactory factory, string name, object? value)
    {
        if (value is null) return;
        // Short-circuit on empty strings too so we don't pollute log events with blanks.
        if (value is string s && string.IsNullOrEmpty(s)) return;
        logEvent.AddOrUpdateProperty(factory.CreateProperty(name, value));
    }
}
