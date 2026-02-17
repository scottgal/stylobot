using Mostlylucid.StyloSpam.Core.Extensions;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;
using Mostlylucid.StyloSpam.Outgoing.Models;
using Mostlylucid.StyloSpam.Outgoing.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStyloSpamScoring(options => options.DefaultMode = EmailFlowMode.Outgoing);
builder.Services.Configure<EmailScoringOptions>(builder.Configuration.GetSection("EmailScoring"));
builder.Services.AddSingleton<IUserSendHistoryStore, InMemoryUserSendHistoryStore>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "StyloSpam Outgoing",
    mode = "outgoing",
    description = "Pass-through semantic filter to prevent platform users from sending spam"
}));

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/outgoing/users/{tenantId}/{userId}/stats", (string tenantId, string userId, IUserSendHistoryStore history) =>
{
    var stats = history.GetStats(tenantId, userId);
    return Results.Ok(stats);
});

app.MapPost("/outgoing/filter/raw", async (
    OutgoingRawFilterRequest request,
    IUserSendHistoryStore history,
    EmailScoringEngine engine,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.RawMime))
    {
        return Results.BadRequest("UserId and RawMime are required.");
    }

    var tenantId = request.TenantId ?? "default";
    var sentLastHour = history.RecordAndCountLastHour(tenantId, request.UserId, DateTimeOffset.UtcNow);

    var metadata = request.Metadata ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    metadata["outgoing.user_messages_last_hour"] = sentLastHour;

    var envelope = EmailEnvelopeFactory.FromRawMime(
        request.RawMime,
        EmailFlowMode.Outgoing,
        tenantId: tenantId,
        userId: request.UserId,
        metadata: metadata);

    var score = await engine.EvaluateAsync(envelope, cancellationToken);
    return Results.Ok(ToDecision(score, request.UserId, tenantId, sentLastHour));
});

app.MapPost("/outgoing/filter/simple", async (
    OutgoingSimpleFilterRequest request,
    IUserSendHistoryStore history,
    EmailScoringEngine engine,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.From) || request.To is null || request.To.Count == 0)
    {
        return Results.BadRequest("UserId, From, and at least one To recipient are required.");
    }

    var tenantId = request.TenantId ?? "default";
    var sentLastHour = history.RecordAndCountLastHour(tenantId, request.UserId, DateTimeOffset.UtcNow);

    var metadata = request.Metadata ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    metadata["outgoing.user_messages_last_hour"] = sentLastHour;

    var envelope = EmailEnvelopeFactory.FromSimpleFields(
        EmailFlowMode.Outgoing,
        request.From,
        request.To ?? [],
        request.Subject,
        request.TextBody,
        request.HtmlBody,
        tenantId: tenantId,
        userId: request.UserId,
        headers: request.Headers,
        attachments: request.Attachments,
        metadata: metadata);

    var score = await engine.EvaluateAsync(envelope, cancellationToken);
    return Results.Ok(ToDecision(score, request.UserId, tenantId, sentLastHour));
});

app.Run();

static OutgoingFilterDecision ToDecision(
    EmailScoreResult score,
    string userId,
    string tenantId,
    int sentLastHour)
{
    var action = score.Verdict switch
    {
        SpamVerdict.Allow => "PassThrough",
        SpamVerdict.Tag => "PassThroughWithHeaderTag",
        SpamVerdict.Warn => "HoldForSecondaryReview",
        SpamVerdict.Quarantine => "QuarantineAndNotify",
        SpamVerdict.Block => "RejectAndAlert",
        _ => "PassThrough"
    };

    return new OutgoingFilterDecision(
        userId,
        tenantId,
        score.Verdict,
        action,
        score.SpamScore,
        score.Confidence,
        score.TopReasons,
        sentLastHour);
}
