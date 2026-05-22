using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Models.Auth;
using Mostlylucid.BotDetection.UI.Notifications;
using Mostlylucid.Notify;
using Mostlylucid.Notify.Email;

namespace Mostlylucid.BotDetection.UI.Services.Auth;

/// <summary>
///     ASP.NET Identity <see cref="IEmailSender{TUser}"/> impl. Now a thin shim over
///     <see cref="INotificationSender"/>: each typed Identity call is routed to one of the
///     three M0 templates (registration.verify, auth.password.reset, auth.mfa.code) with a
///     strongly-typed model rather than the raw HTML+subject path Identity used previously.
/// </summary>
[Obsolete("Use INotificationSender + the typed templates directly. This shim exists for backward compat with ASP.NET Identity's IEmailSender<TUser> contract.")]
public sealed class StyloBotSmtpEmailSender : IEmailSender<StyloBotUser>
{
    private readonly INotificationSender _notify;
    private readonly ILogger<StyloBotSmtpEmailSender> _logger;
    private readonly string _siteName;

    public StyloBotSmtpEmailSender(
        INotificationSender notify,
        IOptions<StyloBotSmtpOptions> smtpOptions,
        ILogger<StyloBotSmtpEmailSender> logger)
    {
        _notify = notify;
        _logger = logger;
        // FromName doubles as the public site label in the templated emails so the existing
        // "StyloBot:Smtp:FromName" knob still controls greeting copy.
        _siteName = string.IsNullOrWhiteSpace(smtpOptions.Value.FromName)
            ? "Stylobot"
            : smtpOptions.Value.FromName;
    }

    public Task SendConfirmationLinkAsync(StyloBotUser user, string email, string confirmationLink) =>
        SendTemplatedAsync(
            email,
            "registration.verify",
            new RegistrationVerifyModel(
                DisplayName: user.UserName ?? email,
                VerifyUrl: confirmationLink,
                SiteName: _siteName));

    public Task SendPasswordResetLinkAsync(StyloBotUser user, string email, string resetLink) =>
        SendTemplatedAsync(
            email,
            "auth.password.reset",
            new PasswordResetModel(
                DisplayName: user.UserName ?? email,
                ResetUrl: resetLink,
                SiteName: _siteName,
                ValidFor: TimeSpan.FromHours(24)));

    public Task SendPasswordResetCodeAsync(StyloBotUser user, string email, string resetCode) =>
        SendTemplatedAsync(
            email,
            "auth.mfa.code",
            new MfaCodeModel(
                DisplayName: user.UserName ?? email,
                Code: resetCode,
                SiteName: _siteName,
                ValidFor: TimeSpan.FromMinutes(15)));

    private async Task SendTemplatedAsync(string email, string template, object model, CancellationToken cancellationToken = default)
    {
        var message = NotificationMessage.Email(
            to: new EmailRecipient(email),
            template: template,
            model: model);

        var result = await _notify.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            _logger.LogWarning(
                "StyloBotSmtpEmailSender: notify send failed for template '{Template}' to {Email}: {Reason}",
                template, email, result.Error ?? "(no detail)");
        }
        else
        {
            _logger.LogInformation(
                "StyloBotSmtpEmailSender: dispatched '{Template}' to {Email} via INotificationSender",
                template, email);
        }
    }
}
