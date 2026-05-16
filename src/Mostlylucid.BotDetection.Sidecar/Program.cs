using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Api;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Sidecar;
using Mostlylucid.BotDetection.Sidecar.Services;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Information)
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var sidecar = SidecarOptions.FromEnvironment();

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"));

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        // Bind loopback by default: a sidecar shares the loopback interface with the
        // app it serves (same pod / same container). Binding all interfaces is opt-in
        // via STYLOBOT_BIND=any and only sensible when callers reach it over a network.
        void Listen(int listenPort, HttpProtocols protocols)
        {
            if (sidecar.BindAnyInterface)
                kestrel.ListenAnyIP(listenPort, opts => opts.Protocols = protocols);
            else
                kestrel.ListenLocalhost(listenPort, opts => opts.Protocols = protocols);
        }

        // gRPC clients use HTTP/2 Prior Knowledge (h2c); Http2 accepts them.
        Listen(sidecar.Port, HttpProtocols.Http2);

        // Mixed mode adds an HTTP/1.1 REST port for callers that can't use gRPC.
        if (!sidecar.GrpcOnly)
            Listen(sidecar.RestPort, HttpProtocols.Http1);
    });

    builder.Services.AddSingleton(sidecar);
    builder.Services.AddSingleton<ApiKeyGrpcAuthenticator>();

    // gRPC - primary high-throughput interface.
    builder.Services.AddGrpc(opts =>
    {
        opts.MaxReceiveMessageSize = 1 * 1024 * 1024; // 1MB: detect requests are tiny
        opts.MaxSendMessageSize = 4 * 1024 * 1024;    // bound RenderWidget output
        opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
        opts.Interceptors.Add<ApiKeyInterceptor>();   // x-sb-api-key enforcement
    });

    // Bot detection (all 49 detectors, SQLite persistence in FOSS).
    builder.Services.AddBotDetection();

    // REST API (for callers that can't use gRPC: Node SDK, curl, etc.).
    if (!sidecar.GrpcOnly)
        builder.Services.AddStyloBotApi(opts => opts.EnableOpenApi = false);

    var app = builder.Build();

    // Secure-by-default gate. The gRPC surface drives the full detection pipeline and
    // mutates shared, persistent reputation state, so exposing it to a network without
    // authentication lets anyone poison detection. Refuse that combination unless it
    // was an explicit opt-in.
    var botOptions = app.Services.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
    var keysConfigured = botOptions.ApiKeys.Count > 0;

    if (sidecar.BindAnyInterface && !keysConfigured && !sidecar.AllowInsecure)
    {
        Log.Fatal("Refusing to start: STYLOBOT_BIND binds all interfaces but no API keys " +
                  "are configured. Configure BotDetection:ApiKeys, leave the listener on " +
                  "loopback (unset STYLOBOT_BIND), or set STYLOBOT_ALLOW_INSECURE=true to " +
                  "deliberately run exposed and unauthenticated.");
        return 1;
    }

    if (!keysConfigured)
        Log.Warning("gRPC API-key authentication is DISABLED - no BotDetection:ApiKeys " +
                    "configured. The detection engine is reachable unauthenticated on the " +
                    "bound interface ({Bind}).",
            sidecar.BindAnyInterface ? "all interfaces" : "loopback");

    app.MapGrpcService<DetectionGrpcService>();
    if (!sidecar.GrpcOnly)
        app.MapStyloBotApi();
    app.MapGet("/health", () => Results.Ok(new
       {
           status = "healthy",
           mode = sidecar.GrpcOnly ? "sidecar-grpc" : "sidecar",
           port = sidecar.Port,
       }))
       .AllowAnonymous();

    Log.Information("StyloBot sidecar starting: gRPC :{Port} ({Bind}){Rest}, auth {Auth}",
        sidecar.Port,
        sidecar.BindAnyInterface ? "all interfaces" : "loopback",
        sidecar.GrpcOnly ? string.Empty : $", REST :{sidecar.RestPort}",
        keysConfigured ? "enabled" : "DISABLED");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Sidecar failed to start");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;
