namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     Resolves a page manifest into a single <c>ComposeBatchAsync</c> call and
///     returns the composed dataset wrapped in a <see cref="DashboardPageResult"/>.
/// </summary>
public interface IDashboardPageComposer
{
    /// <summary>
    ///     Composes all datasets required by the widgets listed in <paramref name="manifest"/>
    ///     in a single batched store call, and returns the result.
    /// </summary>
    Task<DashboardPageResult> ComposeAsync(
        DashboardPageManifest manifest,
        DashboardPageWindow window,
        CancellationToken ct);
}
