namespace AeroDB;

/// <summary>
/// Base class for graph edge types. Edges are SurrealDB records with In/Out direction.
/// Extends SurrealDb.Net.Models.RelationRecord to gain IRelationRecord interface and CBOR compatibility.
/// </summary>
public abstract class EdgeRecord : SurrealDb.Net.Models.RelationRecord
{
}
