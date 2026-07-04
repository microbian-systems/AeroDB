namespace Dali.IdGeneration;

/// <summary>
/// Defines a contract for generating unique, time-sortable long identifiers
/// (e.g., Snowflake IDs).
/// </summary>
public interface IIdGenerator
{
    /// <summary>
    /// Generates a new unique identifier.
    /// </summary>
    /// <returns>A unique long identifier.</returns>
    long NewId();
}
