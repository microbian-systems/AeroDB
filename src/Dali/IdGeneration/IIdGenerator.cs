namespace Dali.IdGeneration;

/// <summary>
/// Generates long primary keys for entities that have not been assigned an ID.
/// Implementations include Snowflake, Hilo, SequentialGuid, etc.
/// </summary>
public interface IIdGenerator
{
    /// <summary>
    /// Generates a new unique long identifier.
    /// </summary>
    long NewId();
}
