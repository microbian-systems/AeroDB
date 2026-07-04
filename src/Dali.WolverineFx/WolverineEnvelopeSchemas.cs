using AeroDB;

namespace Dali.WolverineFx;

/// <summary>
/// Registers Wolverine envelope table schemas on <see cref="StoreOptions.Schema"/>
/// during <see cref="DocumentStore.InitializeAsync"/> via the <see cref="IConfigureDali"/> hook.
/// This replaces the hardcoded 131-line SchemaScript string in <see cref="DaliMessageStore"/>
/// with proper typed POCOs and the AeroDB schema pipeline.
/// </summary>
public sealed class WolverineEnvelopeSchemas : IConfigureDali
{
    /// <summary>
    /// Registers all six Wolverine envelope tables with their indexes
    /// on the AeroDB <see cref="StoreOptions"/>.
    /// </summary>
    public void Configure(StoreOptions options)
    {
        // Wolverine incoming envelopes
        options.Schema.For<WolverineIncomingEnvelope>()
            .SetSchemaMode(SchemaMode.Strict)
            .Index(x => x.Status)
            .Index(x => x.ExecutionTime)
            .Index(x => x.OwnerId);

        // Wolverine outgoing envelopes
        options.Schema.For<WolverineOutgoingEnvelope>()
            .SetSchemaMode(SchemaMode.Strict)
            .Index(x => x.Destination);

        // Wolverine dead letters
        options.Schema.For<WolverineDeadLetterEnvelope>()
            .SetSchemaMode(SchemaMode.Strict)
            .Index(x => x.Status);

        // Wolverine nodes
        options.Schema.For<WolverineNode>()
            .SetSchemaMode(SchemaMode.Strict)
            .UniqueIndex(x => x.Id);

        // Wolverine agent restrictions (no indexes needed — queried by full scan)
        options.Schema.For<WolverineAgentRestrictions>()
            .SetSchemaMode(SchemaMode.Strict);

        // Wolverine node records (no indexes needed — ordered by timestamp)
        options.Schema.For<WolverineNodeRecords>()
            .SetSchemaMode(SchemaMode.Strict);
    }
}
