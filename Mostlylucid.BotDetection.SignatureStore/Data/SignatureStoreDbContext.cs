using Microsoft.EntityFrameworkCore;
using Mostlylucid.BotDetection.SignatureStore.Models;

namespace Mostlylucid.BotDetection.SignatureStore.Data;

/// <summary>
/// DbContext for storing bot detection signatures in Postgres.
/// Uses JSONB with GIN indexes for efficient querying by any signal.
/// </summary>
public class SignatureStoreDbContext : DbContext
{
    public SignatureStoreDbContext(DbContextOptions<SignatureStoreDbContext> options)
        : base(options)
    {
    }

    public DbSet<SignatureEntity> Signatures { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SignatureEntity>(entity =>
        {
            // Primary key
            entity.HasKey(e => e.SignatureId);

            // Indexes for common queries
            entity.HasIndex(e => e.Timestamp)
                .HasDatabaseName("ix_signatures_timestamp");

            entity.HasIndex(e => e.BotProbability)
                .HasDatabaseName("ix_signatures_bot_probability");

            entity.HasIndex(e => e.Confidence)
                .HasDatabaseName("ix_signatures_confidence");

            entity.HasIndex(e => e.RiskBand)
                .HasDatabaseName("ix_signatures_risk_band");

            entity.HasIndex(e => e.RequestPath)
                .HasDatabaseName("ix_signatures_request_path");

            entity.HasIndex(e => e.RemoteIp)
                .HasDatabaseName("ix_signatures_remote_ip");

            entity.HasIndex(e => e.UserAgent)
                .HasDatabaseName("ix_signatures_user_agent");

            entity.HasIndex(e => e.BotName)
                .HasDatabaseName("ix_signatures_bot_name");

            entity.HasIndex(e => e.PolicyName)
                .HasDatabaseName("ix_signatures_policy_name");

            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("ix_signatures_created_at");

            entity.HasIndex(e => e.ExpiresAt)
                .HasDatabaseName("ix_signatures_expires_at")
                .HasFilter("expires_at IS NOT NULL");

            // GIN index on JSONB signature_json for fast querying by any field
            entity.HasIndex(e => e.SignatureJson)
                .HasDatabaseName("ix_signatures_json_gin")
                .HasMethod("gin");

            // GIN index on JSONB signals_json for fast signal queries
            entity.HasIndex(e => e.SignalsJson)
                .HasDatabaseName("ix_signatures_signals_gin")
                .HasMethod("gin")
                .HasFilter("signals_json IS NOT NULL");

            // Composite indexes for common query patterns
            entity.HasIndex(e => new { e.Timestamp, e.BotProbability })
                .HasDatabaseName("ix_signatures_timestamp_botprob");

            entity.HasIndex(e => new { e.RiskBand, e.Timestamp })
                .HasDatabaseName("ix_signatures_riskband_timestamp");
        });
    }
}
