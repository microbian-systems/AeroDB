namespace AeroDB.IdGeneration;

/// <summary>
/// Snowflake ID generator wrapping <see cref="Aero.Core.Snowflake.NewId"/>.
/// Produces unique, time-sortable long primary keys.
/// Uses the FlakeId algorithm underneath Aero.Core.
/// </summary>
public sealed class SnowflakeIdGenerator : IIdGenerator
{
    /// <summary>
    /// Creates a Snowflake generator using Aero.Core's default machine ID
    /// (randomly assigned from 1–1023 via <c>RandomNumberGenerator</c>).
    /// </summary>
    public SnowflakeIdGenerator()
    {
    }

    /// <summary>
    /// Creates a Snowflake generator with a specific machine ID (0–1023).
    /// Useful for deterministic setups (e.g., Kubernetes pods with stable ordinals).
    /// </summary>
    /// <param name="machineId">Machine/worker ID (0–1023).</param>
    public SnowflakeIdGenerator(int machineId)
    {
        Aero.Core.Snowflake.SetMachineId(machineId);
    }

    /// <inheritdoc />
    public long NewId() => Aero.Core.Snowflake.NewId();
}
