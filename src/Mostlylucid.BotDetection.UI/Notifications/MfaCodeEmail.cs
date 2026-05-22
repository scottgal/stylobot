using Mostlylucid.Notify;
using SliceProxy = Mostlylucid.BotDetection.UI.Notifications.Slices.MfaCodeEmail;

namespace Mostlylucid.BotDetection.UI.Notifications;

public sealed class MfaCodeEmail : INotificationTemplate<MfaCodeModel>
{
    public string Subject(MfaCodeModel m) => $"{m.SiteName} verification code: {m.Code}";

    public async Task<string> RenderHtmlAsync(MfaCodeModel model, CancellationToken cancellationToken = default)
    {
        var slice = SliceProxy.Create(model);
        await using var writer = new StringWriter();
        await slice.RenderAsync(writer, cancellationToken: cancellationToken);
        return writer.ToString();
    }

    public Task<string> RenderTextAsync(MfaCodeModel m, CancellationToken cancellationToken = default)
    {
        var validity = m.ValidFor.TotalHours >= 1
            ? $"{m.ValidFor.TotalHours:F0} hours"
            : $"{m.ValidFor.TotalMinutes:F0} minutes";
        return Task.FromResult($"""
            Your {m.SiteName} verification code

            Hi {m.DisplayName},

            Code: {m.Code}
            Valid for {validity}.

            If this wasn't you, change your password and contact support.
            """);
    }
}
