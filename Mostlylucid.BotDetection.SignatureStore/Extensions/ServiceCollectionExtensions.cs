using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.SignatureStore.Data;
using Mostlylucid.BotDetection.SignatureStore.Hubs;
using Mostlylucid.BotDetection.SignatureStore.Middleware;
using Mostlylucid.BotDetection.SignatureStore.Repositories;
using Mostlylucid.BotDetection.SignatureStore.Services;

namespace Mostlylucid.BotDetection.SignatureStore.Extensions;

/// <summary>
/// Extension methods for registering SignatureStore services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add SignatureStore services to DI container.
    /// Includes: DbContext, Repository, SignalR hub, Broadcaster, Cleanup service
    /// </summary>
    public static IServiceCollection AddSignatureStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind configuration
        var options = new SignatureStoreOptions();
        configuration.GetSection(SignatureStoreOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // Only register services if enabled
        if (!options.Enabled)
        {
            return services;
        }

        // Resolve connection string (with environment variable substitution)
        var connectionString = options.GetConnectionString();

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "SignatureStore is enabled but no connection string is configured. " +
                $"Set '{SignatureStoreOptions.SectionName}:ConnectionString' in appsettings.json");
        }

        // Register DbContext with Postgres
        services.AddDbContext<SignatureStoreDbContext>(opts =>
        {
            opts.UseNpgsql(connectionString, npgsqlOpts =>
            {
                npgsqlOpts.EnableRetryOnFailure(maxRetryCount: 3);
                npgsqlOpts.CommandTimeout(30);
            });
        });

        // Register repository
        services.AddScoped<ISignatureRepository, SignatureRepository>();

        // Register SignalR broadcaster
        services.AddSingleton<ISignatureBroadcaster, SignatureBroadcaster>();

        // Register SignalR hub if enabled
        if (options.EnableSignalR)
        {
            services.AddSignalR();
        }

        // Register cleanup service if enabled
        if (options.EnableAutoCleanup)
        {
            services.AddHostedService<SignatureCleanupService>();
        }

        return services;
    }

    /// <summary>
    /// Add SignatureStore middleware to the pipeline.
    /// Should be called AFTER bot detection middleware.
    /// </summary>
    public static IApplicationBuilder UseSignatureStore(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetService<SignatureStoreOptions>();

        if (options?.Enabled != true)
        {
            return app;
        }

        // Ensure database is created (for development)
        using (var scope = app.ApplicationServices.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SignatureStoreDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<SignatureStoreDbContext>>();

            try
            {
                logger.LogInformation("Ensuring SignatureStore database exists...");
                dbContext.Database.EnsureCreated();
                logger.LogInformation("SignatureStore database ready");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize SignatureStore database");
                throw;
            }
        }

        // Add middleware
        app.UseMiddleware<SignatureStoreMiddleware>();

        // Map SignalR hub if enabled
        if (options.EnableSignalR)
        {
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHub<SignatureHub>(options.SignalRHubPath);
            });
        }

        return app;
    }

    /// <summary>
    /// Map SignatureStore API endpoints.
    /// Provides REST API for querying signatures.
    /// </summary>
    public static IEndpointRouteBuilder MapSignatureStoreApi(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetService<SignatureStoreOptions>();

        if (options?.Enabled != true || !options.EnableApiEndpoints)
        {
            return endpoints;
        }

        var basePath = options.ApiBasePath.TrimEnd('/');

        // GET /api/signatures/recent?count=100
        endpoints.MapGet($"{basePath}/recent", async (
            ISignatureRepository repository,
            int count = 100) =>
        {
            var signatures = await repository.GetRecentAsync(count);
            return Results.Ok(signatures);
        })
        .WithName("GetRecentSignatures");

        // GET /api/signatures/top?count=100
        endpoints.MapGet($"{basePath}/top", async (
            ISignatureRepository repository,
            int count = 100) =>
        {
            var signatures = await repository.GetTopByBotProbabilityAsync(count);
            return Results.Ok(signatures);
        })
        .WithName("GetTopSignatures");

        // GET /api/signatures/{id}
        endpoints.MapGet($"{basePath}/{{id}}", async (
            string id,
            ISignatureRepository repository) =>
        {
            var signature = await repository.GetByIdAsync(id);
            return signature != null ? Results.Ok(signature) : Results.NotFound();
        })
        .WithName("GetSignatureById");

        // GET /api/signatures/stats
        endpoints.MapGet($"{basePath}/stats", async (ISignatureRepository repository) =>
        {
            var stats = await repository.GetStatsAsync();
            return Results.Ok(stats);
        })
        .WithName("GetSignatureStats");

        // GET /api/signatures/filter?signalPath=signals.ua.headless_detected&signalValue=true
        endpoints.MapGet($"{basePath}/filter", async (
            string signalPath,
            string? signalValue,
            int count,
            int offset,
            ISignatureRepository repository) =>
        {
            // Parse signalValue to appropriate type
            object? parsedValue = null;
            if (!string.IsNullOrEmpty(signalValue))
            {
                if (bool.TryParse(signalValue, out var boolVal))
                    parsedValue = boolVal;
                else if (double.TryParse(signalValue, out var doubleVal))
                    parsedValue = doubleVal;
                else
                    parsedValue = signalValue;
            }

            var signatures = await repository.GetBySignalFilterAsync(
                signalPath, parsedValue, count, offset);

            return Results.Ok(signatures);
        })
        .WithName("FilterSignatures");

        // GET /api/signatures/by-risk-band/{riskBand}
        endpoints.MapGet($"{basePath}/by-risk-band/{{riskBand}}", async (
            string riskBand,
            int count,
            int offset,
            ISignatureRepository repository) =>
        {
            var signatures = await repository.GetByRiskBandAsync(riskBand, count, offset);
            return Results.Ok(signatures);
        })
        .WithName("GetSignaturesByRiskBand");

        return endpoints;
    }
}
