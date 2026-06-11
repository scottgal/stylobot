using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     <c>sb-scope-picker</c> -- form-based picker for the composite
///     <see cref="Mostlylucid.BotDetection.Policies.Rules.PolicyScope"/>
///     shape (<c>Host?</c> / <c>Method?</c> / <c>Geo?</c> /
///     <c>Identity?</c>). Each axis is independently nullable. The picker
///     renders four select-driven rows plus their supporting inputs, then
///     mirrors the chosen values into a hidden field carrying the JSON
///     projection of the resulting <c>PolicyScope</c>. The wrapping form
///     posts that JSON under <see cref="ScopePickerViewModel.FieldName"/>
///     so the server-side handler binds it as a single payload regardless
///     of which axes the operator touched.
///
///     <para>
///         <c>none</c> on every axis renders as a wildcard scope (matches
///         every request). The picker degrades cleanly without JavaScript:
///         each axis surfaces its raw inputs so an operator can still pick
///         a Host kind + Method + Geo + Identity by clicking the controls,
///         and the embedded Alpine wrapper rebuilds the hidden JSON on
///         every change.
///     </para>
///
///     <para>
///         The control is FOSS so the commercial Apply Template modal
///         (T4 Part C) can call it once for each per-application scope.
///         The view component takes no DI dependencies -- the model is
///         declarative -- so the commercial layer registers no overrides.
///     </para>
/// </summary>
public sealed class SbScopePickerViewComponent : ViewComponent
{
    /// <summary>
    ///     Build the picker. <paramref name="model"/> is optional; when
    ///     <c>null</c> the picker renders the wildcard form with default
    ///     method / geo / identity dropdowns.
    /// </summary>
    public IViewComponentResult Invoke(ScopePickerViewModel? model = null)
        => View(model ?? new ScopePickerViewModel());
}
