namespace Mostlylucid.BotDetection.UI.Configuration;

/// <summary>
///     The two debounce timings the policy-stack edit row exposes to
///     <c>policy-stack-edit.js</c> via <c>data-*</c> attributes on the
///     <c>.sb-policy-stack-edit-row</c> article. Held as a FOSS-narrow
///     POCO so:
///
///     <list type="bullet">
///         <item>
///             A self-hosted FOSS gateway keeps the historical literal
///             behaviour (80 / 500 ms) without touching anything --
///             <see cref="PolicyEditPresenter"/> populates the view model
///             from the default values declared on this type.
///         </item>
///         <item>
///             Commercial wires the wider <c>PolicyStackEditorOptions</c>
///             onto this narrower one via <c>Configure&lt;T&gt;</c>, so
///             an operator that tweaks <c>BotDetection:Commercial:PolicyEditor:ParseDebounceMs</c>
///             in <c>appsettings.json</c> sees the change reach the JS
///             without a FOSS code edit.
///         </item>
///     </list>
///
///     The JS reads <c>article.dataset.parseDebounceMs</c> / <c>backtestDebounceMs</c>
///     with a <c>parseInt() || &lt;literal&gt;</c> fallback so an empty
///     attribute (the FOSS render path that doesn't populate the view
///     model fields at all) still works.
/// </summary>
public sealed class PolicyEditTimingsOptions
{
    /// <summary>
    ///     Milliseconds the textarea waits after the last keystroke before
    ///     POSTing to <c>/dashboard/policystack/parse</c>. 80ms matches the
    ///     historical FOSS literal; the wider commercial spec default is
    ///     200ms (operators that want the spec value bind via the
    ///     commercial options class).
    /// </summary>
    public int ParseDebounceMs { get; set; } = 80;

    /// <summary>
    ///     Milliseconds the editor waits after a successful parse before
    ///     re-running the C8 backtest projection. 500ms matches the
    ///     historical FOSS literal.
    /// </summary>
    public int BacktestDebounceMs { get; set; } = 500;
}