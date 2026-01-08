using Microsoft.EntityFrameworkCore;

namespace Mostlylucid.GeoDetection.Data;

/// <summary>
///     EF Core DbContext for caching geo lookups
///     Uses SQLite by default, but can be configured to use any EF provider
/// </summary>
public class GeoDbContext : DbContext
{
    public GeoDbContext(DbContextOptions<GeoDbContext> options) : base(options)
    {
    }

    public DbSet<CachedGeoLocation> CachedLocations => Set<CachedGeoLocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CachedGeoLocation>(entity =>
        {
            entity.HasKey(e => e.IpAddress);
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasIndex(e => e.CountryCode);

            entity.Property(e => e.IpAddress).HasMaxLength(45); // IPv6 max length
            entity.Property(e => e.CountryCode).HasMaxLength(2);
            entity.Property(e => e.CountryName).HasMaxLength(100);
            entity.Property(e => e.ContinentCode).HasMaxLength(2);
            entity.Property(e => e.RegionCode).HasMaxLength(10);
            entity.Property(e => e.City).HasMaxLength(100);
            entity.Property(e => e.TimeZone).HasMaxLength(50);
        });
    }
}

/// <summary>
///     Cached geo location entry
/// </summary>
public class CachedGeoLocation
{
    /// <summary>
    ///     IP address (primary key)
    /// </summary>
    public string IpAddress { get; set; } = "";

    /// <summary>
    ///     ISO 3166-1 alpha-2 country code
    /// </summary>
    public string CountryCode { get; set; } = "";

    /// <summary>
    ///     Full country name
    /// </summary>
    public string CountryName { get; set; } = "";

    /// <summary>
    ///     Continent code
    /// </summary>
    public string? ContinentCode { get; set; }

    /// <summary>
    ///     Region/state code
    /// </summary>
    public string? RegionCode { get; set; }

    /// <summary>
    ///     City name
    /// </summary>
    public string? City { get; set; }

    /// <summary>
    ///     Latitude
    /// </summary>
    public double? Latitude { get; set; }

    /// <summary>
    ///     Longitude
    /// </summary>
    public double? Longitude { get; set; }

    /// <summary>
    ///     IANA timezone
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    ///     Is a known VPN/proxy
    /// </summary>
    public bool IsVpn { get; set; }

    /// <summary>
    ///     Is a hosting/datacenter IP
    /// </summary>
    public bool IsHosting { get; set; }

    /// <summary>
    ///     When this entry was cached
    /// </summary>
    public DateTime CachedAt { get; set; }

    /// <summary>
    ///     When this entry expires
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    ///     Which provider was used to look this up
    /// </summary>
    public string? Provider { get; set; }
}