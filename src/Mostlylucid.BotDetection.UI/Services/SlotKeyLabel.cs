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
}
