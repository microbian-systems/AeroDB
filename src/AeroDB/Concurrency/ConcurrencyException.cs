namespace AeroDB;

/// <summary>
/// Thrown when an optimistic concurrency check fails —
/// the document was modified by another session between load and save.
/// </summary>
public class ConcurrencyException : InvalidOperationException
{
    public Type DocumentType { get; }
    public object? DocumentId { get; }
    public long ExpectedVersion { get; }
    public long ActualVersion { get; }

    public ConcurrencyException(Type documentType, object? documentId, long expectedVersion, long actualVersion)
        : base(
            $"Concurrency conflict on {documentType.Name} (id={documentId}): " +
            $"expected version {expectedVersion}, but found version {actualVersion}.")
    {
        DocumentType = documentType;
        DocumentId = documentId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    /// <summary>
    /// Stream-based constructor for event sourcing concurrency conflicts.
    /// </summary>
    public ConcurrencyException(string streamId, long expectedVersion, long actualVersion)
        : base(
            $"Concurrency conflict on stream '{streamId}': " +
            $"expected version {expectedVersion}, but found version {actualVersion}.")
    {
        DocumentType = typeof(string);
        DocumentId = streamId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
