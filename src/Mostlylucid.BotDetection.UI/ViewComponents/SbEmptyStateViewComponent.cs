using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     The dashboard's shared "nothing here yet" card. See <see cref="EmptyStateViewModel"/> for
///     the parameter shapes this replaces (previously hand-duplicated per widget, or in several
///     places a bare unstyled paragraph with no icon/card at all).
/// </summary>
public sealed class SbEmptyStateViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(EmptyStateViewModel model) => View(model);
}
