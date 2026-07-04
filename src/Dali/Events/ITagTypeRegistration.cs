namespace Dali;

/// <summary>
/// Registration of a tag type for dynamic consistency boundary (DCB) projections.
/// Tags allow events across different streams to be grouped by a shared domain identity.
/// </summary>
public interface ITagTypeRegistration
{
    /// <summary>Extracts the tag value from a tag object.</summary>
    string ExtractValue(object tag);

    /// <summary>The .NET type of the tag value.</summary>
    Type TagType { get; }

    /// <summary>The aggregate type that the tag routes to.</summary>
    Type? AggregateType { get; }
}
