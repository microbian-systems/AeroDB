namespace AeroDB;

/// <summary>
/// Interface for entities with a typed primary key.
/// Use <see cref="Entity{TId}"/> as a base class for default implementation.
/// For Snowflake-style long IDs, use <see cref="Entity{TId}"/> with <c>long</c>.
/// </summary>
/// <typeparam name="TId">The primary key type (long, string, int, Guid, etc.).</typeparam>
public interface IEntity<TId>
    where TId : notnull, IEquatable<TId>, IComparable<TId>
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
    where TId : notnull, IEquatable<TId>, IComparable<TId>
{
    /// <summary>
    /// The primary key for this entity.
    /// </summary>
    public TId Id { get; set; } = default!;

    /// <summary>
    /// Returns the string representation of the primary key, or a fallback for reference-type keys that have not been set.
    /// </summary>
    public override string? ToString()
    {
        return Id?.ToString() ?? "value was null";
    }
}

/// <summary>
/// Convenience alias for the most common case: a <c>long</c> primary key entity
/// using Snowflake ID generation.
/// </summary>
public abstract class EntitySnowlake : Entity<long>
{
    protected EntitySnowlake() => Id = SnowflakeGenerator.NewId();
}

/// <summary>
/// Convenience alias for a <c>string</c> primary key entity
/// using Snowflake ID generation converted to string.
/// </summary>
public abstract class EntityString : Entity<string>
{
    protected EntityString() => Id = SnowflakeGenerator.NewId().ToString();
}

/// <summary>
/// Convenience alias for an <c>int</c> primary key entity
/// using a random integer.
/// </summary>
public abstract class EntityInt : Entity<int>
{
    private static readonly Random _random = new();
    protected EntityInt() => Id = _random.Next(1, int.MaxValue);
}

/// <summary>
/// Convenience alias for a <c>Guid</c> primary key entity
/// using a new random GUID.
/// </summary>
public abstract class EntityGuid : Entity<Guid>
{
    protected EntityGuid() => Id = Guid.NewGuid();
}

/// <summary>
/// Static helper for generating Snowflake IDs.
/// </summary>
public static class SnowflakeGenerator
{
    /// <summary>
    /// Creates a new Snowflake ID using the FlakeId library.
    /// </summary>
    public static long NewId() => FlakeId.Id.Create();
}
