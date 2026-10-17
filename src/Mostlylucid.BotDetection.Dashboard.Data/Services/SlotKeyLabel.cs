using Mostlylucid.BotDetection.Policies.Signals;

namespace Mostlylucid.BotDetection.UI.Services;

public static class SlotKeyLabel
{
    public static string ToHumanLabel(string slotKey)
    {
        if (string.IsNullOrEmpty(slotKey)) return string.Empty;
        var dot = slotKey.LastIndexOf('.');
        var tail = dot >= 0 ? slotKey[(dot + 1)..] : slotKey;
        return tail.Replace('_', ' ');
    }

    /// <summary>
    ///     Two-stage label resolution shared by every drift / mode-shift surface
    ///     (T17 contract): catalogue Short text wins when the slot has a
    ///     descriptor with non-blank Short copy; otherwise the structural
    ///     normalisation in <see cref="ToHumanLabel"/> takes over. Catalogue
    ///     may be null on a remote-mode dashboard host that didn't register
    ///     <see cref="ISignalCatalog"/> — the structural fallback still
    ///     produces a readable label so the badge tooltip never goes blank.
    /// </summary>
    public static string ResolveOrNormalize(string? slotKey, ISignalCatalog? catalog)
    {
        if (string.IsNullOrEmpty(slotKey)) return string.Empty;
        var d = catalog?.TryGet(slotKey);
        if (d is not null && !string.IsNullOrWhiteSpace(d.Short)) return d.Short;
        return ToHumanLabel(slotKey);
    }
}
