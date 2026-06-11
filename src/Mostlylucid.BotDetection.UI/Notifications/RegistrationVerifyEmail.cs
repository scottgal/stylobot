using Mostlylucid.Notify;
using SliceProxy = Mostlylucid.BotDetection.UI.Notifications.Slices.RegistrationVerifyEmail;

namespace Mostlylucid.BotDetection.UI.Notifications;

public sealed class RegistrationVerifyEmail : INotificationTemplate<RegistrationVerifyModel>
{
    public string Subject(RegistrationVerifyModel m) => $"Verify your email: {m.SiteName}";

    public async Task<string> RenderHtmlAsync(RegistrationVerifyModel model, CancellationToken cancellationToken = default)
    {
        var slice = SliceProxy.Create(model);
        await using var writer = new StringWriter();
        await slice.RenderAsync(writer, cancellationToken: cancellationToken);
        return writer.ToString();
    }

    public Task<string> RenderTextAsync(RegistrationVerifyModel m, CancellationToken cancellationToken = default) =>
        Task.FromResult($"""
            Welcome to {m.SiteName}

            Hi {m.DisplayName},

            Verify your email by opening this link:
            {m.VerifyUrl}

            If you didn't sign up, ignore this message.
            """);
}
