namespace Dali.Tests;

using Dali.WolverineFx;
using NSubstitute;
using TUnit.Core;
using Wolverine;

/// <summary>
/// Pure unit tests for Layer 3 of Dali.WolverineFx:
///  • DaliOps factory methods (Store/Delete/Insert)
///  • ScopedDocumentSessionHolder
///  • WolverineEnvelopeSchemas.Configure()
///
/// No Wolverine runtime, no SurrealDB, no DI — only NSubstitute mocks.
/// </summary>
public class DaliOpsTests
{
    // ====================================================================
    // 1. DaliOps factory methods
    // ====================================================================

    public class MyEntity
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    [Test]
    public async Task DaliOps_Store_Returns_StoreOp_With_Correct_Entity()
    {
        var entity = new MyEntity { Id = "1", Name = "test" };
        var op = DaliOps.Store(entity);

        op.ShouldNotBeNull();
        op.ShouldBeAssignableTo<IDaliOp>();
        op.ShouldBeAssignableTo<ISideEffect>();

        // ExecuteAsync should call session.Store(entity)
        var session = Substitute.For<IDocumentSession>();
        await op.ExecuteAsync(session, CancellationToken.None);

        session.Received(1).Store(entity);
    }

    [Test]
    public async Task DaliOps_Delete_Returns_DeleteOp_With_Correct_Entity()
    {
        var entity = new MyEntity { Id = "2", Name = "delete-me" };
        var op = DaliOps.Delete(entity);

        op.ShouldNotBeNull();
        op.ShouldBeAssignableTo<IDaliOp>();
        op.ShouldBeAssignableTo<ISideEffect>();

        var session = Substitute.For<IDocumentSession>();
        await op.ExecuteAsync(session, CancellationToken.None);

        session.Received(1).Delete(entity);
    }

    [Test]
    public async Task DaliOps_Insert_Returns_InsertOp_With_Correct_Entity()
    {
        var entity = new MyEntity { Id = "3", Name = "insert-me" };
        var op = DaliOps.Insert(entity);

        op.ShouldNotBeNull();
        op.ShouldBeAssignableTo<IDaliOp>();
        op.ShouldBeAssignableTo<ISideEffect>();

        var session = Substitute.For<IDocumentSession>();
        await op.ExecuteAsync(session, CancellationToken.None);

        session.Received(1).Store(entity);
    }

    [Test]
    public async Task DaliOps_Store_DifferentEntities_Are_Independent()
    {
        var entity1 = new MyEntity { Id = "a", Name = "first" };
        var entity2 = new MyEntity { Id = "b", Name = "second" };

        var op1 = DaliOps.Store(entity1);
        var op2 = DaliOps.Store(entity2);

        var session = Substitute.For<IDocumentSession>();
        await op1.ExecuteAsync(session, CancellationToken.None);
        await op2.ExecuteAsync(session, CancellationToken.None);

        session.Received(1).Store(entity1);
        session.Received(1).Store(entity2);
    }

    [Test]
    public async Task DaliOps_Delete_DifferentEntities_Are_Independent()
    {
        var entity1 = new MyEntity { Id = "x", Name = "del1" };
        var entity2 = new MyEntity { Id = "y", Name = "del2" };

        var op1 = DaliOps.Delete(entity1);
        var op2 = DaliOps.Delete(entity2);

        var session = Substitute.For<IDocumentSession>();
        await op1.ExecuteAsync(session, CancellationToken.None);
        await op2.ExecuteAsync(session, CancellationToken.None);

        session.Received(1).Delete(entity1);
        session.Received(1).Delete(entity2);
    }

    [Test]
    public async Task DaliOps_Insert_DoesNotCallDelete()
    {
        var entity = new MyEntity { Id = "i1", Name = "insert-only" };
        var op = DaliOps.Insert(entity);

        var session = Substitute.For<IDocumentSession>();
        await op.ExecuteAsync(session, CancellationToken.None);

        session.DidNotReceive().Delete(Arg.Any<MyEntity>());
    }

    [Test]
    public async Task DaliOps_Store_DoesNotCallDelete()
    {
        var entity = new MyEntity { Id = "s1", Name = "store-only" };
        var op = DaliOps.Store(entity);

        var session = Substitute.For<IDocumentSession>();
        await op.ExecuteAsync(session, CancellationToken.None);

        session.DidNotReceive().Delete(Arg.Any<MyEntity>());
    }


    // ====================================================================
    // 2. ScopedDocumentSessionHolder
    // ====================================================================

    [Test]
    public void ScopedDocumentSessionHolder_Session_DefaultIsNull()
    {
        var holder = new ScopedDocumentSessionHolder();
        holder.Session.ShouldBeNull();
    }

    [Test]
    public void ScopedDocumentSessionHolder_Session_SetAndGet()
    {
        var holder = new ScopedDocumentSessionHolder();
        var mockSession = Substitute.For<IDocumentSession>();

        holder.Session = mockSession;

        holder.Session.ShouldNotBeNull();
        holder.Session.ShouldBe(mockSession);
    }

    [Test]
    public void ScopedDocumentSessionHolder_Session_CanBeSetToNull()
    {
        var holder = new ScopedDocumentSessionHolder();
        var mockSession = Substitute.For<IDocumentSession>();

        holder.Session = mockSession;
        holder.Session.ShouldNotBeNull();

        holder.Session = null;
        holder.Session.ShouldBeNull();
    }

    [Test]
    public void ScopedDocumentSessionHolder_Session_Reassignable()
    {
        var holder = new ScopedDocumentSessionHolder();
        var session1 = Substitute.For<IDocumentSession>();
        var session2 = Substitute.For<IDocumentSession>();

        holder.Session = session1;
        holder.Session.ShouldBe(session1);

        holder.Session = session2;
        holder.Session.ShouldBe(session2);
        holder.Session.ShouldNotBe(session1);
    }

    // ====================================================================
    // 3. WolverineEnvelopeSchemas.Configure()
    // ====================================================================

    [Test]
    public void WolverineEnvelopeSchemas_Registers_AllSixTables()
    {
        var options = new StoreOptions();
        var configurator = new WolverineEnvelopeSchemas();

        configurator.Configure(options);

        // After Configure, 6 mappings should be registered
        options.Schema.Mappings.Count.ShouldBe(6);
    }

    [Test]
    public void WolverineEnvelopeSchemas_Includes_WolverineIncomingEnvelope()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        options.Schema.Mappings.ShouldContainKey(typeof(WolverineIncomingEnvelope));
    }

    [Test]
    public void WolverineEnvelopeSchemas_Includes_WolverineOutgoingEnvelope()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        options.Schema.Mappings.ShouldContainKey(typeof(WolverineOutgoingEnvelope));
    }

    [Test]
    public void WolverineEnvelopeSchemas_Includes_WolverineDeadLetterEnvelope()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        options.Schema.Mappings.ShouldContainKey(typeof(WolverineDeadLetterEnvelope));
    }

    [Test]
    public void WolverineEnvelopeSchemas_Includes_WolverineNode()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        options.Schema.Mappings.ShouldContainKey(typeof(WolverineNode));
    }

    [Test]
    public void WolverineEnvelopeSchemas_Includes_WolverineAgentRestrictions()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        options.Schema.Mappings.ShouldContainKey(typeof(WolverineAgentRestrictions));
    }

    [Test]
    public void WolverineEnvelopeSchemas_Includes_WolverineNodeRecords()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        options.Schema.Mappings.ShouldContainKey(typeof(WolverineNodeRecords));
    }

    [Test]
    public void WolverineEnvelopeSchemas_Incoming_Has_StrictSchema()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineIncomingEnvelope>)options.Schema.Mappings[typeof(WolverineIncomingEnvelope)];
        mapping.SchemaModeType.ShouldBe(SchemaMode.Strict);
    }

    [Test]
    public void WolverineEnvelopeSchemas_Incoming_Has_StatusIndex()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineIncomingEnvelope>)options.Schema.Mappings[typeof(WolverineIncomingEnvelope)];
        mapping.Indices.ShouldContain(idx => idx.Columns.Contains("Status"));
    }

    [Test]
    public void WolverineEnvelopeSchemas_Incoming_Has_ExecutionTimeIndex()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineIncomingEnvelope>)options.Schema.Mappings[typeof(WolverineIncomingEnvelope)];
        mapping.Indices.ShouldContain(idx => idx.Columns.Contains("ExecutionTime"));
    }

    [Test]
    public void WolverineEnvelopeSchemas_Incoming_Has_OwnerIdIndex()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineIncomingEnvelope>)options.Schema.Mappings[typeof(WolverineIncomingEnvelope)];
        mapping.Indices.ShouldContain(idx => idx.Columns.Contains("OwnerId"));
    }

    [Test]
    public void WolverineEnvelopeSchemas_Outgoing_Has_StrictSchema()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineOutgoingEnvelope>)options.Schema.Mappings[typeof(WolverineOutgoingEnvelope)];
        mapping.SchemaModeType.ShouldBe(SchemaMode.Strict);
    }

    [Test]
    public void WolverineEnvelopeSchemas_Outgoing_Has_DestinationIndex()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineOutgoingEnvelope>)options.Schema.Mappings[typeof(WolverineOutgoingEnvelope)];
        mapping.Indices.ShouldContain(idx => idx.Columns.Contains("Destination"));
    }

    [Test]
    public void WolverineEnvelopeSchemas_DeadLetter_Has_StrictSchema()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineDeadLetterEnvelope>)options.Schema.Mappings[typeof(WolverineDeadLetterEnvelope)];
        mapping.SchemaModeType.ShouldBe(SchemaMode.Strict);
    }

    [Test]
    public void WolverineEnvelopeSchemas_DeadLetter_Has_StatusIndex()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineDeadLetterEnvelope>)options.Schema.Mappings[typeof(WolverineDeadLetterEnvelope)];
        mapping.Indices.ShouldContain(idx => idx.Columns.Contains("Status"));
    }

    [Test]
    public void WolverineEnvelopeSchemas_Node_Has_StrictSchema()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineNode>)options.Schema.Mappings[typeof(WolverineNode)];
        mapping.SchemaModeType.ShouldBe(SchemaMode.Strict);
    }

    [Test]
    public void WolverineEnvelopeSchemas_Node_Has_UniqueIdIndex()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineNode>)options.Schema.Mappings[typeof(WolverineNode)];
        // UniqueIndex creates an index with IsUnique = true
        mapping.Indices.ShouldContain(idx => idx.Columns.Contains("Id") && idx.IsUnique);
    }

    [Test]
    public void WolverineEnvelopeSchemas_AgentRestrictions_Has_StrictSchema()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineAgentRestrictions>)options.Schema.Mappings[typeof(WolverineAgentRestrictions)];
        mapping.SchemaModeType.ShouldBe(SchemaMode.Strict);
    }

    [Test]
    public void WolverineEnvelopeSchemas_AgentRestrictions_Has_NoIndices()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineAgentRestrictions>)options.Schema.Mappings[typeof(WolverineAgentRestrictions)];
        mapping.Indices.ShouldBeEmpty();
    }

    [Test]
    public void WolverineEnvelopeSchemas_NodeRecords_Has_StrictSchema()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineNodeRecords>)options.Schema.Mappings[typeof(WolverineNodeRecords)];
        mapping.SchemaModeType.ShouldBe(SchemaMode.Strict);
    }

    [Test]
    public void WolverineEnvelopeSchemas_NodeRecords_Has_NoIndices()
    {
        var options = new StoreOptions();
        new WolverineEnvelopeSchemas().Configure(options);

        var mapping = (DocumentMapping<WolverineNodeRecords>)options.Schema.Mappings[typeof(WolverineNodeRecords)];
        mapping.Indices.ShouldBeEmpty();
    }

    [Test]
    public void WolverineEnvelopeSchemas_DoubleCall_DoesNotDuplicateRegistrations()
    {
        var options = new StoreOptions();
        var configurator = new WolverineEnvelopeSchemas();

        configurator.Configure(options);
        configurator.Configure(options);

        // Schema.For<T>() uses TryGetValue pattern, so duplicate calls are idempotent
        options.Schema.Mappings.Count.ShouldBe(6);
    }

    [Test]
    public void WolverineEnvelopeSchemas_Implements_IConfigureDali()
    {
        var configurator = new WolverineEnvelopeSchemas();
        configurator.ShouldBeAssignableTo<IConfigureDali>();
    }
}
