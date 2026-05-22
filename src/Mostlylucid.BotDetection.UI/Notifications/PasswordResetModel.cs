namespace Mostlylucid.BotDetection.UI.Notifications;

public sealed record PasswordResetModel(string DisplayName, string ResetUrl, string SiteName, TimeSpan ValidFor);
