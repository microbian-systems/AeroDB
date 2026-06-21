namespace Dali;

/// <summary>
/// Interface for entities with a typed primary key.
/// Use <see cref="Entity{TId}"/> as a base class for default implementation.
/// For Snowflake-style long IDs, use <see cref="IEntity{long}"/>.
/// </summary>
/// <typeparam name="TId">The primary key type (long, string, int, Guid, etc.).</typeparam>
public interface IEntity<TId>
{
    /// <summary>
    /// The primary key for this entity.
    /// </summary>
    TId Id { get; set; }
}

/// <summary>
/// Abstract base class for entities with a typed primary key.
/// Implements <see cref="IEntity{TId}"/> with a simple auto-property.
/// </summary>
/// <typeparam name="TId">The primary key type.</typeparam>
public abstract class Entity<TId> : IEntity<TId>
{
    /// <summary>
    /// The primary key for this entity.
    /// </summary>
    public TId Id { get; set; }

    /// <summary>
    /// Returns the string representation of the primary key, or null for reference-type keys that have not been set.
    /// </summary>
    public override string? ToString()
    {
        return Id?.ToString();
    }
}

/// <summary>
/// Convenience alias for the most common case: a <c>long</c> primary key entity.
/// </summary>
public abstract class Entity : Entity<long>
{
}
