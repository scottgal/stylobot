using Mostlylucid.Notify;
using SliceProxy = Mostlylucid.BotDetection.UI.Notifications.Slices.PasswordResetEmail;

namespace Mostlylucid.BotDetection.UI.Notifications;

public sealed class PasswordResetEmail : INotificationTemplate<PasswordResetModel>
{
    public string Subject(PasswordResetModel m) => $"Reset your {m.SiteName} password";

    public async Task<string> RenderHtmlAsync(PasswordResetModel model, CancellationToken cancellationToken = default)
    {
        var slice = SliceProxy.Create(model);
        await using var writer = new StringWriter();
        await slice.RenderAsync(writer, cancellationToken: cancellationToken);
        return writer.ToString();
    }

    public Task<string> RenderTextAsync(PasswordResetModel m, CancellationToken cancellationToken = default) =>
        Task.FromResult($"""
            Reset your {m.SiteName} password

            Hi {m.DisplayName},

            Open this link within {m.ValidFor.TotalMinutes:F0} minutes to reset your password:
            {m.ResetUrl}

            If you didn't request this, ignore the message.
            """);
}
