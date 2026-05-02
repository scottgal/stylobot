namespace Mostlylucid.BotDetection.UI.Services.Auth;

public sealed class StyloBotSmtpOptions
{
    public const string Section = "StyloBot:Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "StyloBot Dashboard";
}
