namespace AeroDB;

/// <summary>Metadata about an event stream.</summary>
public class StreamState
{
    /// <summary>The stream identifier.</summary>
    public string StreamId { get; set; } = "";

    /// <summary>The current version (total number of events) of the stream.</summary>
    public long Version { get; set; }

    /// <summary>Timestamp of the first event in the stream.</summary>
    public DateTimeOffset? Created { get; set; }

    /// <summary>Timestamp of the most recent event in the stream.</summary>
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>Whether the stream has been archived.</summary>
    public bool IsArchived { get; set; }

    /// <summary>The aggregate type of the stream, if known.</summary>
    public string? AggregateType { get; set; }
}
