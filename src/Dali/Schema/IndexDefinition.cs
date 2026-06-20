namespace Dali;

/// <summary>
/// Defines a SurrealDB index (simple, unique, or composite).
/// </summary>
public class IndexDefinition
{
    public string Name { get; set; } = "";
    public string[] Columns { get; set; } = [];
    public bool IsUnique { get; set; }
}

/// <summary>
/// Configures an index being built via the fluent <see cref="DocumentMapping{T}"/> API.
/// </summary>
public class IndexOptions
{
    internal IndexOptions(IndexDefinition definition) => Definition = definition;
    internal IndexDefinition Definition { get; }

    public void IsUnique() => Definition.IsUnique = true;
    public void WithName(string name) => Definition.Name = name;
}
