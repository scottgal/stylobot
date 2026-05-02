using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Models.Auth;

namespace Mostlylucid.BotDetection.UI.Services.Auth;

public sealed class StyloBotDevEmailSender : IEmailSender<StyloBotUser>
{
    private readonly ILogger<StyloBotDevEmailSender> _logger;

    public StyloBotDevEmailSender(ILogger<StyloBotDevEmailSender> logger) => _logger = logger;

    public Task SendConfirmationLinkAsync(StyloBotUser user, string email, string confirmationLink)
    {
        _logger.LogWarning("[StyloBot Dev Email] Confirmation link for {Email}: {Link}", email, confirmationLink);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(StyloBotUser user, string email, string resetLink)
    {
        _logger.LogWarning("[StyloBot Dev Email] Password reset link for {Email}: {Link}", email, resetLink);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(StyloBotUser user, string email, string resetCode)
    {
        _logger.LogWarning("[StyloBot Dev Email] Password reset code for {Email}: {Code}", email, resetCode);
        return Task.CompletedTask;
    }
}
