using SurrealDb.Net.Models;

namespace AeroDB.Sable;

/// <summary>
/// Configures polymorphic document hierarchies.
/// Maps a base document type to its subclasses.
/// </summary>
public class DocumentHierarchy
{
    public Type BaseType { get; }
    public HashSet<Type> SubTypes { get; } = new();

    public DocumentHierarchy(Type baseType)
    {
        BaseType = baseType;
    }

    /// <summary>
    /// Register a subclass of the base document type.
    /// </summary>
    public DocumentHierarchy AddSubClass<T>() where T : Record
    {
        SubTypes.Add(typeof(T));
        return this;
    }
}
