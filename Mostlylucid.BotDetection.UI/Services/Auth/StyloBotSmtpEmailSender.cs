using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Models.Auth;

namespace Mostlylucid.BotDetection.UI.Services.Auth;

public sealed class StyloBotSmtpEmailSender : IEmailSender<StyloBotUser>
{
    private readonly StyloBotSmtpOptions _smtp;
    private readonly ILogger<StyloBotSmtpEmailSender> _logger;

    public StyloBotSmtpEmailSender(
        IOptions<StyloBotSmtpOptions> options,
        ILogger<StyloBotSmtpEmailSender> logger)
    {
        _smtp = options.Value;
        _logger = logger;
    }

    public Task SendConfirmationLinkAsync(StyloBotUser user, string email, string confirmationLink) =>
        SendAsync(email, "Confirm your StyloBot Dashboard account",
            $"<p>Please confirm your account by <a href='{HtmlEncode(confirmationLink)}'>clicking here</a>.</p>" +
            $"<p>Or copy this link: {HtmlEncode(confirmationLink)}</p>");

    public Task SendPasswordResetLinkAsync(StyloBotUser user, string email, string resetLink) =>
        SendAsync(email, "Reset your StyloBot Dashboard password",
            $"<p>Reset your password by <a href='{HtmlEncode(resetLink)}'>clicking here</a>.</p>" +
            $"<p>This link expires in 24 hours.</p>");

    public Task SendPasswordResetCodeAsync(StyloBotUser user, string email, string resetCode) =>
        SendAsync(email, "Your StyloBot Dashboard verification code",
            $"<p>Your verification code is: <strong style='font-size:1.4em;letter-spacing:2px'>{HtmlEncode(resetCode)}</strong></p>" +
            $"<p>This code expires in 15 minutes.</p>");

    private async Task SendAsync(string to, string subject, string htmlBody)
    {
        if (string.IsNullOrEmpty(_smtp.Host))
        {
            _logger.LogWarning(
                "SMTP not configured - email to {To} dropped. Set {Section}:Host in appsettings.json.",
                to, StyloBotSmtpOptions.Section);
            return;
        }

        using var client = new SmtpClient(_smtp.Host, _smtp.Port)
        {
            EnableSsl = _smtp.EnableSsl,
            Credentials = _smtp.Username != null
                ? new System.Net.NetworkCredential(_smtp.Username, _smtp.Password)
                : null
        };

        var fromAddress = _smtp.FromAddress ?? $"noreply@{_smtp.Host}";
        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, _smtp.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        message.To.Add(to);

        await client.SendMailAsync(message);
        _logger.LogInformation("Email sent to {To}: {Subject}", to, subject);
    }

    private static string HtmlEncode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);
}
