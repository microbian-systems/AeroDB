namespace AeroDB.Metadata;

/// <summary>
/// Marks a document as having auto-tracked audit metadata.
/// Properties are populated automatically by <see cref="Diagnostics.DocumentMetadataListener"/>
/// during <see cref="IDocumentSession.SaveChangesAsync(System.Threading.CancellationToken)"/>.
/// </summary>
public interface IDocumentMetadata
{
    /// <summary>
    /// The UTC timestamp when this document was first created.
    /// Set once on initial store and preserved on subsequent updates.
    /// </summary>
    DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The UTC timestamp of the most recent update to this document.
    /// Updated on every store operation (both insert and update).
    /// </summary>
    DateTimeOffset? LastModified { get; set; }

    /// <summary>
    /// Identity of the user who last modified this document.
    /// Auto-populated by <see cref="Diagnostics.DocumentMetadataListener"/> from
    /// <see cref="InternalSessionBase.CurrentUser"/>.
    /// </summary>
    string? LastModifiedBy { get; set; }
}
