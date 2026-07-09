namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     External-package hook for contributing a named dataset to the dashboard page
///     composer, so pack/commercial widgets (compliance, otel-mesh, sessions, …) join
///     the tick-warmed content bundle instead of self-fetching on the request thread.
///     Mirrors the C4a <c>IEndpointPolicyRuleExtension</c> seam: FOSS knows only the
///     kind NAME and dispatches; it ships zero implementations. Packs register their
///     own via DI (<c>IEnumerable&lt;IDashboardDatasetExtension&gt;</c>).
///
///     <para>
///         The extension resolves its data where it is registered — typically the
///         website (remote viewer), wrapping the pack's existing remote provider — so
///         the composed bundle stays local; only the extension's own fetch crosses to
///         the gateway, and that happens out-of-band on the materializer tick. There
///         is deliberately no polymorphic serialization of the whole bundle.
///     </para>
///
///     <para>Fail-closed: an extension that throws yields a null slice, never a 500.</para>
/// </summary>
public interface IDashboardDatasetExtension
{
    /// <summary>The dataset-kind name this extension resolves (e.g. <c>"compliance-status"</c>). Matches the widget's <c>[DashboardWidget(key, extensionKind)]</c>.</summary>
    string DatasetKind { get; }

    /// <summary>
    ///     Resolves this extension's data for the requested window. The returned object
    ///     is the widget's own view model; the consuming ViewComponent casts it via
    ///     <see cref="DashboardPageResult.GetExtension{T}"/>. Null means "no data" and
    ///     the widget falls back to its self-fetch.
    /// </summary>
    Task<object?> ResolveAsync(DashboardDatasetContext context, CancellationToken ct);
}

/// <summary>
///     The window + filter context handed to an <see cref="IDashboardDatasetExtension"/>,
///     mirroring the fields of <c>DashboardBatchRequest</c> so an extension sees the same
///     window the batched datasets were composed for.
/// </summary>
public readonly record struct DashboardDatasetContext(
    DateTime? StartTime,
    DateTime? EndTime,
    string? AudienceFilter,
    IReadOnlyList<string>? Domains,
    IReadOnlyDictionary<string, string>? Parameters);
