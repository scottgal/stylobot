using Microsoft.EntityFrameworkCore;

namespace Stylobot.Gateway.Data;

/// <summary>
/// Gateway database context for storing routes and configuration.
/// </summary>
public class GatewayDbContext : DbContext
{
    public GatewayDbContext(DbContextOptions<GatewayDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Stored route configurations.
    /// </summary>
    public DbSet<RouteEntity> Routes => Set<RouteEntity>();

    /// <summary>
    /// Stored cluster configurations.
    /// </summary>
    public DbSet<ClusterEntity> Clusters => Set<ClusterEntity>();

    /// <summary>
    /// Cluster destinations.
    /// </summary>
    public DbSet<DestinationEntity> Destinations => Set<DestinationEntity>();

    /// <summary>
    /// Configuration key-value store.
    /// </summary>
    public DbSet<ConfigEntry> ConfigEntries => Set<ConfigEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RouteEntity>(entity =>
        {
            entity.HasKey(e => e.RouteId);
            entity.Property(e => e.RouteId).HasMaxLength(100);
            entity.Property(e => e.ClusterId).HasMaxLength(100);
            entity.Property(e => e.MatchPath).HasMaxLength(500);
            entity.Property(e => e.MatchHostsJson).HasMaxLength(1000);
            entity.Property(e => e.MatchMethodsJson).HasMaxLength(200);
            entity.Property(e => e.TransformsJson).HasMaxLength(4000);
            entity.Property(e => e.MetadataJson).HasMaxLength(4000);
        });

        modelBuilder.Entity<ClusterEntity>(entity =>
        {
            entity.HasKey(e => e.ClusterId);
            entity.Property(e => e.ClusterId).HasMaxLength(100);
            entity.Property(e => e.LoadBalancingPolicy).HasMaxLength(50);
            entity.Property(e => e.HealthCheckJson).HasMaxLength(2000);
            entity.Property(e => e.MetadataJson).HasMaxLength(4000);
            entity.HasMany(e => e.Destinations)
                  .WithOne(d => d.Cluster)
                  .HasForeignKey(d => d.ClusterId);
        });

        modelBuilder.Entity<DestinationEntity>(entity =>
        {
            entity.HasKey(e => new { e.ClusterId, e.DestinationId });
            entity.Property(e => e.ClusterId).HasMaxLength(100);
            entity.Property(e => e.DestinationId).HasMaxLength(100);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.Health).HasMaxLength(500);
            entity.Property(e => e.MetadataJson).HasMaxLength(4000);
        });

        modelBuilder.Entity<ConfigEntry>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(200);
            entity.Property(e => e.Value).HasMaxLength(4000);
        });
    }
}

/// <summary>
/// Route entity for database storage.
/// </summary>
public class RouteEntity
{
    public required string RouteId { get; set; }
    public required string ClusterId { get; set; }
    public string? MatchPath { get; set; }
    public string? MatchHostsJson { get; set; }
    public string? MatchMethodsJson { get; set; }
    public int Order { get; set; }
    public string? AuthorizationPolicy { get; set; }
    public string? RateLimiterPolicy { get; set; }
    public string? TransformsJson { get; set; }
    public string? MetadataJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Cluster entity for database storage.
/// </summary>
public class ClusterEntity
{
    public required string ClusterId { get; set; }
    public string? LoadBalancingPolicy { get; set; }
    public string? HealthCheckJson { get; set; }
    public string? MetadataJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<DestinationEntity> Destinations { get; set; } = new List<DestinationEntity>();
}

/// <summary>
/// Destination entity for database storage.
/// </summary>
public class DestinationEntity
{
    public required string ClusterId { get; set; }
    public required string DestinationId { get; set; }
    public required string Address { get; set; }
    public string? Health { get; set; }
    public string? MetadataJson { get; set; }

    public ClusterEntity? Cluster { get; set; }
}

/// <summary>
/// Generic configuration key-value entry.
/// </summary>
public class ConfigEntry
{
    public required string Key { get; set; }
    public string? Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
