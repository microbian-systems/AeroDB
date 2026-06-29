namespace Dali;

/// <summary>
/// Defines a foreign key relationship for informational/validation purposes.
/// SurrealDB uses graph edges instead of FK cascades, so this metadata is
/// stored for tooling and documentation only — no DDL is generated.
/// </summary>
public class ForeignKeyDefinition
{
    /// <summary>The property name on the source type that holds the foreign key.</summary>
    public string PropertyName { get; }

    /// <summary>The related (child) document type.</summary>
    public Type ChildType { get; }

    /// <summary>Whether deletes should cascade to related records. Informational only.</summary>
    public bool CascadeDelete { get; set; }

    /// <summary>Optional constraint name for documentation.</summary>
    public string? ConstraintName { get; set; }

    public ForeignKeyDefinition(string propertyName, Type childType)
    {
        PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
        ChildType = childType ?? throw new ArgumentNullException(nameof(childType));
    }
}
