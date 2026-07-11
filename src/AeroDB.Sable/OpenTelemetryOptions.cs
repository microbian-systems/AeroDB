namespace AeroDB.Sable;

/// <summary>Configuration for OpenTelemetry instrumentation of AeroDB.Sable operations.</summary>
public class OpenTelemetryOptions
{
    /// <summary>Whether to track document store operations (session open, save, query).</summary>
    public bool TrackDocumentStore { get; set; } = true;

    /// <summary>Whether to track individual database commands.</summary>
    public bool TrackCommands { get; set; } = true;

    /// <summary>Whether to include the SurrealQL statement text in span tags.</summary>
    public bool IncludeSqlStatements { get; set; } = false;

    /// <summary>Whether to track event sourcing operations (append, fetch).</summary>
    public bool TrackEvents { get; set; } = true;

    /// <summary>Whether to track projection execution.</summary>
    public bool TrackProjections { get; set; } = true;
}
