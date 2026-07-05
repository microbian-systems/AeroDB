namespace AeroDB;

/// <summary>
/// Controls which endpoint is used for read operations.
/// </summary>
public enum ReadPreference
{
    /// <summary>Use the primary (write-capable) endpoint for reads.</summary>
    Primary = 0,

    /// <summary>Prefer secondary (read-only) endpoints for reads.</summary>
    Secondary = 1,

    /// <summary>Use the nearest endpoint (lowest latency).</summary>
    Nearest = 2
}
