using AeroDB.Sable;

namespace AeroDB.WolverineFx;

/// <summary>
/// Registers Wolverine envelope table schemas on <see cref="StoreOptions.Schema"/>
/// during <see cref="DocumentStore.InitializeAsync"/> via the <see cref="IConfigureAeroDB"/> hook.
/// This replaces the hardcoded 131-line SchemaScript string in <see cref="AeroDBMessageStore"/>
/// with proper typed POCOs and the AeroDB.Sable schema pipeline.
/// </summary>
public sealed class WolverineEnvelopeSchemas : IConfigureAeroDB
{
    /// <summary>
    /// Registers all six Wolverine envelope tables with their indexes
    /// on the AeroDB.Sable <see cref="StoreOptions"/>.
    /// </summary>
    public void Configure(StoreOptions options)
    {
        // Wolverine incoming envelopes
        options.Schema.For<WolverineIncomingEnvelope>()
            .TableName("wolverine_incoming_envelopes")
            .SetSchemaMode(SchemaMode.Strict)
            .Index(x => x.Status)
            .Index(x => x.ExecutionTime)
            .Index(x => x.OwnerId);

        // Wolverine outgoing envelopes
        options.Schema.For<WolverineOutgoingEnvelope>()
            .TableName("wolverine_outgoing_envelopes")
            .SetSchemaMode(SchemaMode.Strict)
            .Index(x => x.Destination);

        // Wolverine dead letters
        options.Schema.For<WolverineDeadLetterEnvelope>()
            .TableName("wolverine_dead_letters")
            .SetSchemaMode(SchemaMode.Strict)
            .Index(x => x.Status);

        // Wolverine nodes
        options.Schema.For<WolverineNode>()
            .TableName("wolverine_nodes")
            .SetSchemaMode(SchemaMode.Strict)
            .UniqueIndex(x => x.Id);

        // Wolverine agent restrictions (no indexes needed — queried by full scan)
        options.Schema.For<WolverineAgentRestrictions>()
            .TableName("wolverine_agent_restrictions")
            .SetSchemaMode(SchemaMode.Strict);

        // Wolverine node records (no indexes needed — ordered by timestamp)
        options.Schema.For<WolverineNodeRecords>()
            .TableName("wolverine_node_records")
            .SetSchemaMode(SchemaMode.Strict);
    }
}
