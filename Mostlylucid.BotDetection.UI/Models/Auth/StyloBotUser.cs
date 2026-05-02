using Microsoft.AspNetCore.Identity;

namespace Mostlylucid.BotDetection.UI.Models.Auth;

public sealed class StyloBotUser : IdentityUser
{
    public string? AuthenticatorKey { get; set; }
    public string? RecoveryCodesJson { get; set; }
}
