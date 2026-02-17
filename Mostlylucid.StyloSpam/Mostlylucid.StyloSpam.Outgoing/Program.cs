using MimeKit;
using Mostlylucid.StyloSpam.Core.Extensions;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;
using Mostlylucid.StyloSpam.Outgoing.Configuration;
using Mostlylucid.StyloSpam.Outgoing.Models;
using Mostlylucid.StyloSpam.Outgoing.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStyloSpamScoring(options => options.DefaultMode = EmailFlowMode.Outgoing);
builder.Services.Configure<EmailScoringOptions>(builder.Configuration.GetSection("EmailScoring"));
builder.Services.Configure<StyloSpamOutgoingOptions>(builder.Configuration.GetSection("StyloSpam:Outgoing"));

builder.Services.AddSingleton<IUserSendHistoryStore, InMemoryUserSendHistoryStore>();
builder.Services.AddSingleton<IOutgoingAbuseGuard, InMemoryOutgoingAbuseGuard>();
builder.Services.AddSingleton<OutgoingRelayService>();

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
    IOutgoingAbuseGuard guard,
    EmailScoringEngine engine,
    CancellationToken cancellationToken) =>
{
    var scored = await ScoreRawAsync(request, history, guard, engine, cancellationToken);
    return scored is null ? Results.BadRequest("UserId and RawMime are required.") : Results.Ok(scored);
});

app.MapPost("/outgoing/filter/simple", async (
    OutgoingSimpleFilterRequest request,
    IUserSendHistoryStore history,
    IOutgoingAbuseGuard guard,
    EmailScoringEngine engine,
    CancellationToken cancellationToken) =>
{
    var scored = await ScoreSimpleAsync(request, history, guard, engine, cancellationToken);
    return scored is null ? Results.BadRequest("UserId, From, and at least one To recipient are required.") : Results.Ok(scored);
});

app.MapPost("/outgoing/filter-and-relay/raw", async (
    OutgoingRawFilterRequest request,
    IUserSendHistoryStore history,
    IOutgoingAbuseGuard guard,
    EmailScoringEngine engine,
    OutgoingRelayService relay,
    CancellationToken cancellationToken) =>
{
    var decision = await ScoreRawAsync(request, history, guard, engine, cancellationToken);
    if (decision is null)
    {
        return Results.BadRequest("UserId and RawMime are required.");
    }

    if (!relay.CanRelay(decision.Verdict))
    {
        return Results.Ok(new OutgoingRelayDecision(decision, false, false, "Policy blocked relay"));
    }

    var message = ParseMime(request.RawMime);
    StampRelayHeaders(message, decision);
    var relayed = await relay.RelayAsync(message, cancellationToken);

    return Results.Ok(new OutgoingRelayDecision(
        decision,
        true,
        relayed,
        relayed ? "Relayed" : "Relay failed or disabled"));
});

app.MapPost("/outgoing/filter-and-relay/simple", async (
    OutgoingSimpleFilterRequest request,
    IUserSendHistoryStore history,
    IOutgoingAbuseGuard guard,
    EmailScoringEngine engine,
    OutgoingRelayService relay,
    CancellationToken cancellationToken) =>
{
    var decision = await ScoreSimpleAsync(request, history, guard, engine, cancellationToken);
    if (decision is null)
    {
        return Results.BadRequest("UserId, From, and at least one To recipient are required.");
    }

    if (!relay.CanRelay(decision.Verdict))
    {
        return Results.Ok(new OutgoingRelayDecision(decision, false, false, "Policy blocked relay"));
    }

    var message = CreateMessageFromSimple(request);
    StampRelayHeaders(message, decision);
    var relayed = await relay.RelayAsync(message, cancellationToken);

    return Results.Ok(new OutgoingRelayDecision(
        decision,
        true,
        relayed,
        relayed ? "Relayed" : "Relay failed or disabled"));
});

app.Run();

static async Task<OutgoingFilterDecision?> ScoreRawAsync(
    OutgoingRawFilterRequest request,
    IUserSendHistoryStore history,
    IOutgoingAbuseGuard guard,
    EmailScoringEngine engine,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.RawMime))
    {
        return null;
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
    return ToDecision(score, request.UserId, tenantId, sentLastHour, guard);
}

static async Task<OutgoingFilterDecision?> ScoreSimpleAsync(
    OutgoingSimpleFilterRequest request,
    IUserSendHistoryStore history,
    IOutgoingAbuseGuard guard,
    EmailScoringEngine engine,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.From) || request.To is null || request.To.Count == 0)
    {
        return null;
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
    return ToDecision(score, request.UserId, tenantId, sentLastHour, guard);
}

static OutgoingFilterDecision ToDecision(
    EmailScoreResult score,
    string userId,
    string tenantId,
    int sentLastHour,
    IOutgoingAbuseGuard guard)
{
    var guardEval = guard.EvaluateAndRecord(tenantId, userId, score.Verdict, DateTimeOffset.UtcNow);

    var effectiveVerdict = guardEval.IsBlocked ? SpamVerdict.Block : score.Verdict;

    var action = effectiveVerdict switch
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
        effectiveVerdict,
        action,
        score.SpamScore,
        score.Confidence,
        score.TopReasons,
        sentLastHour,
        guardEval.IsBlocked,
        guardEval.Reason,
        guardEval.BlockedUntilUtc,
        guardEval.CurrentStrikeCount);
}

static MimeMessage ParseMime(string rawMime)
{
    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawMime));
    return MimeMessage.Load(stream);
}

static MimeMessage CreateMessageFromSimple(OutgoingSimpleFilterRequest request)
{
    var message = new MimeMessage();
    message.MessageId = $"<{Guid.NewGuid():N}@stylospam.local>";
    message.From.Add(MailboxAddress.Parse(request.From));

    foreach (var recipient in request.To ?? [])
    {
        if (!string.IsNullOrWhiteSpace(recipient))
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }
    }

    message.Subject = request.Subject ?? string.Empty;
    var bodyBuilder = new BodyBuilder
    {
        TextBody = request.TextBody,
        HtmlBody = request.HtmlBody
    };

    message.Body = bodyBuilder.ToMessageBody();

    if (request.Headers is not null)
    {
        foreach (var (key, value) in request.Headers)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                message.Headers.Replace(key, value);
            }
        }
    }

    return message;
}

static void StampRelayHeaders(MimeMessage message, OutgoingFilterDecision decision)
{
    message.Headers.Replace("X-StyloSpam-Score", decision.SpamScore.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    message.Headers.Replace("X-StyloSpam-Confidence", decision.Confidence.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    message.Headers.Replace("X-StyloSpam-Verdict", decision.Verdict.ToString());
    message.Headers.Replace("X-StyloSpam-Guard-Blocked", decision.GuardBlocked.ToString());
}
