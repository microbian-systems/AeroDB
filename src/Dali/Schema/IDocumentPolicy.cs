namespace Dali;

/// <summary>Plugin interface for global document mapping conventions.</summary>
public interface IDocumentPolicy
{
    /// <summary>Apply this policy to a document mapping during store initialization.</summary>
    void Apply(DocumentMapping mapping);
}
