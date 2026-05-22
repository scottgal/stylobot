namespace Mostlylucid.BotDetection.UI.Notifications;

public sealed record MfaCodeModel(string DisplayName, string Code, string SiteName, TimeSpan ValidFor);
