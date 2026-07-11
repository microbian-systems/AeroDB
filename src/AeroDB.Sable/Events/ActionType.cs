namespace AeroDB.Sable;

/// <summary>
/// Determines what action a projection should take after processing events.
/// Returned by <see cref="InlineProjection{T}.DetermineAction"/>.
/// </summary>
public enum ActionType
{
    /// <summary>Store the projected document (normal case).</summary>
    Store,
    /// <summary>Skip — do nothing (no-op for this batch).</summary>
    Nothing,
    /// <summary>Mark the document as soft-deleted via <see cref="ISoftDeleted"/>.</summary>
    SoftDelete,
    /// <summary>Hard delete the projected document.</summary>
    HardDelete
}
