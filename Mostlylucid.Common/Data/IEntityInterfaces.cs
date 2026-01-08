namespace Mostlylucid.Common.Data;

/// <summary>
///     Entity with creation and update timestamps
/// </summary>
public interface ITimestampedEntity
{
    /// <summary>
    ///     When the entity was created
    /// </summary>
    DateTime CreatedAt { get; set; }

    /// <summary>
    ///     When the entity was last updated
    /// </summary>
    DateTime UpdatedAt { get; set; }
}

/// <summary>
///     Entity that can expire
/// </summary>
public interface IExpirableEntity
{
    /// <summary>
    ///     When the entity expires
    /// </summary>
    DateTime ExpiresAt { get; set; }

    /// <summary>
    ///     Whether the entity has expired
    /// </summary>
    bool IsExpired => DateTime.UtcNow > ExpiresAt;
}

/// <summary>
///     Entity with a unique key (for caching)
/// </summary>
public interface IKeyedEntity
{
    /// <summary>
    ///     Unique key for this entity
    /// </summary>
    string Key { get; set; }
}

/// <summary>
///     Combined interface for cached entities
/// </summary>
public interface ICachedEntity : IKeyedEntity, ITimestampedEntity, IExpirableEntity
{
}

/// <summary>
///     Base implementation for cached entities
/// </summary>
public abstract class CachedEntityBase : ICachedEntity
{
    public string Key { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
}