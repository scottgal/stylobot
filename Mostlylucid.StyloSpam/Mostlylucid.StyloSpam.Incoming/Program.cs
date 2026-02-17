using Mostlylucid.StyloSpam.Core.Extensions;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;
using Mostlylucid.StyloSpam.Incoming.Configuration;
using Mostlylucid.StyloSpam.Incoming.Connectors;
using Mostlylucid.StyloSpam.Incoming.Models;
using Mostlylucid.StyloSpam.Incoming.Services;
using Mostlylucid.StyloSpam.Incoming.Smtp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StyloSpamIncomingOptions>(builder.Configuration.GetSection("StyloSpam:Incoming"));
builder.Services.AddStyloSpamScoring(options => options.DefaultMode = EmailFlowMode.Incoming);
builder.Services.Configure<EmailScoringOptions>(builder.Configuration.GetSection("EmailScoring"));

builder.Services.AddHttpClient("gmail");
builder.Services.AddHttpClient("outlook");

builder.Services.AddSingleton<IIncomingConnector, SmtpIncomingConnector>();
builder.Services.AddSingleton<IIncomingConnector, ImapIncomingConnector>();
builder.Services.AddSingleton<IIncomingConnector, GmailIncomingConnector>();
builder.Services.AddSingleton<IIncomingConnector, OutlookIncomingConnector>();

builder.Services.AddSingleton<IncomingSmtpRelayService>();
builder.Services.AddSingleton<StyloSpamSmtpMessageStore>();

builder.Services.AddHostedService<IncomingConnectorHostedService>();
builder.Services.AddHostedService<SmtpProxyHostedService>();
builder.Services.AddHostedService<ImapPollingHostedService>();
builder.Services.AddHostedService<GmailPollingHostedService>();
builder.Services.AddHostedService<OutlookPollingHostedService>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "StyloSpam Incoming",
    mode = "incoming",
    supports = new[] { "smtp-proxy", "imap", "gmail-api", "outlook-graph" }
}));

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/capabilities", async (IEnumerable<IIncomingConnector> connectors, CancellationToken cancellationToken) =>
{
    var statuses = new List<IncomingConnectorStatus>();
    foreach (var connector in connectors)
    {
        statuses.Add(await connector.GetStatusAsync(cancellationToken));
    }

    return Results.Ok(new
    {
        protocols = new[] { "SMTP", "IMAP", "Gmail API", "Microsoft Graph" },
        connectors = statuses
    });
});

app.MapPost("/incoming/score/raw", async (
    RawMimeScoreRequest request,
    EmailScoringEngine engine,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.RawMime))
    {
        return Results.BadRequest("RawMime is required.");
    }

    var envelope = EmailEnvelopeFactory.FromRawMime(
        request.RawMime,
        EmailFlowMode.Incoming,
        tenantId: request.TenantId,
        metadata: request.Metadata);

    var result = await engine.EvaluateAsync(envelope, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/incoming/score/simple", async (
    SimpleIncomingScoreRequest request,
    EmailScoringEngine engine,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.From) || request.To is null || request.To.Count == 0)
    {
        return Results.BadRequest("From and at least one To recipient are required.");
    }

    var envelope = EmailEnvelopeFactory.FromSimpleFields(
        EmailFlowMode.Incoming,
        request.From,
        request.To ?? [],
        request.Subject,
        request.TextBody,
        request.HtmlBody,
        tenantId: request.TenantId,
        headers: request.Headers,
        attachments: request.Attachments,
        metadata: request.Metadata);

    var result = await engine.EvaluateAsync(envelope, cancellationToken);
    return Results.Ok(result);
});

app.Run();
