using Microsoft.AspNetCore.Server.Kestrel.Core;
using Mostlylucid.BotDetection.Api;
using Mostlylucid.BotDetection.Extensions;
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
    var port = int.Parse(Environment.GetEnvironmentVariable("STYLOBOT_PORT") ?? "5090");
    var grpcOnly = string.Equals(Environment.GetEnvironmentVariable("STYLOBOT_GRPC_ONLY"), "true", StringComparison.OrdinalIgnoreCase);

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"));

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        if (grpcOnly)
        {
            // gRPC-only: HTTP/2 Prior Knowledge (h2c) on a single port.
            // Http1AndHttp2 sends GOAWAY to h2c Prior Knowledge connections; Http2 accepts them.
            kestrel.ListenAnyIP(port, opts => opts.Protocols = HttpProtocols.Http2);
        }
        else
        {
            // Mixed mode: gRPC (h2c) + REST (HTTP/1.1) on separate ports.
            // gRPC clients use Prior Knowledge h2c which requires Http2.
            // REST/health clients use HTTP/1.1 which requires Http1.
            var restPort = int.Parse(Environment.GetEnvironmentVariable("STYLOBOT_REST_PORT") ?? (port + 1).ToString());
            kestrel.ListenAnyIP(port, opts => opts.Protocols = HttpProtocols.Http2);
            kestrel.ListenAnyIP(restPort, opts => opts.Protocols = HttpProtocols.Http1);
            Log.Information("Mixed mode: gRPC on port {GrpcPort}, REST on port {RestPort}", port, restPort);
        }
    });

    // gRPC - primary high-throughput interface
    builder.Services.AddGrpc(opts =>
    {
        opts.MaxReceiveMessageSize = 1 * 1024 * 1024; // 1MB: detect requests are tiny
        opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
    });

    // Bot detection (all 49 detectors, SQLite persistence in FOSS)
    builder.Services.AddBotDetection();

    // REST API (for callers that can't use gRPC: Node SDK, curl, etc.)
    if (!grpcOnly)
        builder.Services.AddStyloBotApi(opts => opts.EnableOpenApi = false);

    var app = builder.Build();

    app.MapGrpcService<DetectionGrpcService>();
    if (!grpcOnly)
        app.MapStyloBotApi();
    app.MapGet("/health", () => Results.Ok(new { status = "healthy", mode = grpcOnly ? "sidecar-grpc" : "sidecar", port }))
       .AllowAnonymous();

    Log.Information("StyloBot sidecar starting on port {Port} ({Mode})", port, grpcOnly ? "gRPC only" : "gRPC + REST");
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
