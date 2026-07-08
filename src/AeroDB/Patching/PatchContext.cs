namespace AeroDB;

/// <summary>
/// Context metadata attached to a patch operation via <see cref="IPatchExpression{T}.WithReason"/>.
/// Available to <see cref="IDocumentSessionListener"/> implementations and future event pipelines.
/// </summary>
public sealed class PatchContext
{
    /// <summary>The business reason for the patch operation. Maps to domain event emission rules.</summary>
    public string? Reason { get; set; }

    /// <summary>The user ID or identity performing the operation.</summary>
    public string? UserId { get; set; }

    /// <summary>Additional metadata key-value pairs.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>UTC timestamp when the patch context was created.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
