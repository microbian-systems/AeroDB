namespace Dali;

/// <summary>
/// Marks a document as supporting soft deletes. When an entity implementing this
/// interface is deleted, Dali sets the DeletedAt/Deleted fields instead of removing the record.
/// Queries automatically filter out soft-deleted documents.
/// </summary>
public interface ISoftDeleted
{
    /// <summary>
    /// Timestamp indicating when the document was soft-deleted, or null if not deleted.
    /// </summary>
    DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Whether the document is soft-deleted.
    /// </summary>
    bool Deleted { get; set; }
}
