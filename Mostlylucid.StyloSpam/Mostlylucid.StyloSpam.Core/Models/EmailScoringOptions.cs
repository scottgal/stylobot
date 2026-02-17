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

    public LocalLlmSemanticOptions LocalLlm { get; set; } = new();
}

public sealed class LocalLlmSemanticOptions
{
    public bool Enabled { get; set; } = false;
    public string Endpoint { get; set; } = "http://localhost:11434/api/generate";
    public string Model { get; set; } = "qwen2.5:3b";
    public int TimeoutMs { get; set; } = 4000;
    public double MinSuspiciousProbability { get; set; } = 0.65;
    public double MaxScoreDelta { get; set; } = 0.45;
    public int MaxBodyChars { get; set; } = 4000;
}
