namespace Mostlylucid.StyloSpam.Core.Models;

public sealed class EmailScoringOptions
{
    public EmailFlowMode DefaultMode { get; set; } = EmailFlowMode.Incoming;
    public double BaselineScore { get; set; } = 0.15;
    public double TagThreshold { get; set; } = 0.35;
    public double WarnThreshold { get; set; } = 0.55;
    public double QuarantineThreshold { get; set; } = 0.75;
    public double BlockThreshold { get; set; } = 0.90;

    public List<string> SpamPhrases { get; set; } =
    [
        "act now",
        "limited time",
        "guaranteed",
        "risk free",
        "click here",
        "urgent action required",
        "verify your account",
        "winner",
        "free money"
    ];

    public int HighRecipientCountThreshold { get; set; } = 50;
    public int ExtremeRecipientCountThreshold { get; set; } = 200;

    public int OutgoingVelocityWarnThresholdPerHour { get; set; } = 100;
    public int OutgoingVelocityQuarantineThresholdPerHour { get; set; } = 300;
}
