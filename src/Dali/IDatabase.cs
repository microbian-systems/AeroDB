namespace Dali;

/// <summary>Metadata about the database this session is connected to.</summary>
public interface IDatabase
{
    /// <summary>The database name.</summary>
    string Name { get; }

    /// <summary>The namespace this database belongs to.</summary>
    string? Namespace { get; }
}
