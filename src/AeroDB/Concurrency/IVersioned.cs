namespace AeroDB;

/// <summary>
/// Marks a document as supporting optimistic concurrency via a version field.
/// </summary>
public interface IVersioned
{
    long Version { get; set; }
}
