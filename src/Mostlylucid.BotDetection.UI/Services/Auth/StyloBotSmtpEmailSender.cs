using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
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

        var fromAddress = _smtp.FromAddress ?? $"noreply@{_smtp.Host}";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_smtp.FromName, fromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(
            _smtp.Host,
            _smtp.Port,
            _smtp.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None);

        if (_smtp.Username != null)
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password);

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        _logger.LogInformation("Email sent to {To}: {Subject}", to, subject);
    }

    private static string HtmlEncode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);
}
