namespace AeroDB;

/// <summary>
/// Configuration for a single SurrealDB endpoint in a multi-host setup.
/// </summary>
public class DatabaseEndpoint
{
    /// <summary>Endpoint URL (e.g., http://localhost:8000).</summary>
    public string Endpoint { get; set; } = "http://localhost:8000";

    /// <summary>Optional namespace override.</summary>
    public string? Namespace { get; set; }

    /// <summary>Optional database override.</summary>
    public string? Database { get; set; }

    /// <summary>Whether this endpoint can serve read queries.</summary>
    public bool AcceptsReads { get; set; } = true;

    /// <summary>Whether this endpoint can accept writes.</summary>
    public bool AcceptsWrites { get; set; } = true;

    /// <summary>Priority for this endpoint (lower = higher priority).</summary>
    public int Priority { get; set; } = 0;
}
