namespace Mostlylucid.StyloSpam.Outgoing.Configuration;

public sealed class StyloSpamOutgoingOptions
{
    public OutgoingRelayOptions Relay { get; set; } = new();
    public OutgoingAbuseGuardOptions AbuseGuard { get; set; } = new();
}

public sealed class OutgoingRelayOptions
{
    public bool Enabled { get; set; } = false;
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }

    // If false, Warn verdicts are held (not relayed).
    public bool RelayOnWarn { get; set; } = false;
}

public sealed class OutgoingAbuseGuardOptions
{
    public bool Enabled { get; set; } = true;
    public int StrikeThreshold { get; set; } = 3;
    public int StrikeWindowMinutes { get; set; } = 60;
    public int BlockDurationMinutes { get; set; } = 60;
}
