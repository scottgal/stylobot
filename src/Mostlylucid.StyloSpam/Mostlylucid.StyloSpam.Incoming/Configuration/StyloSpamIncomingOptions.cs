namespace Mostlylucid.StyloSpam.Incoming.Configuration;

public sealed class StyloSpamIncomingOptions
{
    public SmtpProxyOptions Smtp { get; set; } = new();
    public ImapIngestionOptions Imap { get; set; } = new();
    public GmailIngestionOptions Gmail { get; set; } = new();
    public OutlookIngestionOptions Outlook { get; set; } = new();
}

public sealed class SmtpProxyOptions
{
    public bool Enabled { get; set; } = true;
    public string ListenHost { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = 2525;

    public string? UpstreamHost { get; set; }
    public int UpstreamPort { get; set; } = 25;
    public bool UseStartTls { get; set; } = true;
    public string? UpstreamUsername { get; set; }
    public string? UpstreamPassword { get; set; }

    public bool RelayEnabled { get; set; } = true;
    public bool QuarantineAsReject { get; set; } = true;
}

public sealed class ImapIngestionOptions
{
    public bool Enabled { get; set; } = true;
    public string? Host { get; set; }
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string Folder { get; set; } = "INBOX";
    public int PollIntervalSeconds { get; set; } = 60;
    public int MaxMessagesPerPoll { get; set; } = 25;
}

public sealed class GmailIngestionOptions
{
    public bool Enabled { get; set; } = true;
    public string? ClientId { get; set; }
    public string? ProjectId { get; set; }
    public string? UserEmail { get; set; }
    public string? AccessToken { get; set; }
    public int PollIntervalSeconds { get; set; } = 90;
    public int MaxMessagesPerPoll { get; set; } = 20;
}

public sealed class OutlookIngestionOptions
{
    public bool Enabled { get; set; } = true;
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? MailboxUserPrincipalName { get; set; }
    public string? AccessToken { get; set; }
    public int PollIntervalSeconds { get; set; } = 90;
    public int MaxMessagesPerPoll { get; set; } = 20;
}
