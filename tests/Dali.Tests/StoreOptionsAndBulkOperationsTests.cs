using NSubstitute;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

// ──────────────────────────────────────────────
// Local test models (not in Models.cs)
// ──────────────────────────────────────────────

internal sealed class FakePolicy : IDocumentPolicy
{
    public bool Applied { get; private set; }
    public void Apply(DocumentMapping mapping) => Applied = true;
}

// ──────────────────────────────────────────────
// Tests
// ──────────────────────────────────────────────

public class StoreOptionsAndBulkOperationsTests
{
    // ──────────────────────────────────────────────
    // Part 1: StoreOptions Configuration Methods
    // ──────────────────────────────────────────────

    [Test]
    public void HierarchyFor_RegistersHierarchy()
    {
        var options = new StoreOptions();

        var hierarchy = options.HierarchyFor<Person>();
        hierarchy.AddSubClass<Product>();

        options.Hierarchies.ShouldContainKey(typeof(Person));
        var registered = options.Hierarchies[typeof(Person)];
        registered.BaseType.ShouldBe(typeof(Person));
        registered.SubTypes.ShouldContain(typeof(Product));
    }

    [Test]
    public void ConfigureSerializer_InvokesAction()
    {
        var options = new StoreOptions();
        var invoked = false;

        options.ConfigureSerializer(opts =>
        {
            invoked = true;
            opts.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        invoked.ShouldBeTrue();
        options.SerializerOptions.ShouldNotBeNull();
    }

    [Test]
    public void AddDatabaseEndpoint_AddsNamedEndpoint()
    {
        var options = new StoreOptions();
        var configured = false;

        options.AddDatabaseEndpoint("http://localhost:8001", ep =>
        {
            configured = true;
            ep.Endpoint.ShouldBe("http://localhost:8001");
        });

        configured.ShouldBeTrue();
        options.DatabaseEndpoints.Count.ShouldBe(1);
        options.DatabaseEndpoints[0].Endpoint.ShouldBe("http://localhost:8001");
    }

    [Test]
    public void Connection_SetsConnectionString()
    {
        var options = new StoreOptions();

        options.Connection(
            "http://remote:9000",
            "prod_ns",
            "prod_db",
            "admin",
            "secret",
            "jwt_token");

        options.Endpoint.ShouldBe("http://remote:9000");
        options.Namespace.ShouldBe("prod_ns");
        options.Database.ShouldBe("prod_db");
        options.Username.ShouldBe("admin");
        options.Password.ShouldBe("secret");
        options.Token.ShouldBe("jwt_token");
    }

    [Test]
    public void EdgeSchemaOptions_ConfiguresEdge()
    {
        var options = new StoreOptions();
        EdgeMapping<Knows>? captured = null;

        options.Schema.Edge<Knows, Person, Person>(mapping =>
        {
            captured = mapping;
            mapping.SetSchemaMode(SchemaMode.Strict);
        });

        captured.ShouldNotBeNull();
        captured.SchemaMode.ShouldBe(SchemaMode.Strict);
        options.Schema.EdgeMappings.Count.ShouldBe(1);
        options.Schema.EdgeMappings[0].ShouldBeSameAs(captured);
    }

    [Test]
    public void EventSourcingOptions_Upcast_RegistersUpcaster()
    {
        var options = new StoreOptions();
        const string oldType = "v1_person_created";

        options.Events.Upcast<Person>(oldType, obj => new Person { Name = "upcasted" });

        options.Events.Upcasters.Count.ShouldBe(1);
        options.Events.Upcasters[0].OldEventType.ShouldBe(oldType);

        var result = options.Events.Upcasters[0].Upcast(new object());
        result.ShouldBeOfType<Person>();
        ((Person)result).Name.ShouldBe("upcasted");
    }

    [Test]
    public void ProjectionOptions_CompositeProjectionFor_Registers()
    {
        var options = new StoreOptions();
        var configured = false;

        var composite = options.ProjectionBuild.CompositeProjectionFor("MyComposite", proj =>
        {
            configured = true;
            proj.Life(ProjectionLifecycle.Live);
        });

        configured.ShouldBeTrue();
        composite.Name.ShouldBe("MyComposite");
        composite.Lifecycle.ShouldBe(ProjectionLifecycle.Live);
        options.ProjectionBuild.CompositeProjections.Count.ShouldBe(1);
        options.ProjectionBuild.CompositeProjections[0].ShouldBeSameAs(composite);
    }

    [Test]
    public void FunctionOptions_Register_NoArgs_StoresFunction()
    {
        var options = new StoreOptions();

        options.Functions.Register("fn::hello", "RETURN 'world'");

        options.Functions.Functions.Count.ShouldBe(1);
        var fn = options.Functions.Functions[0];
        fn.Name.ShouldBe("fn::hello");
        fn.Body.ShouldBe("RETURN 'world'");
        fn.Parameters.ShouldBeNull();
        fn.ParametersTyped.ShouldBeEmpty();
    }

    [Test]
    public void FunctionOptions_Register_WithArgs_StoresFunction()
    {
        var options = new StoreOptions();

        options.Functions.Register("fn::add", "RETURN $a + $b",
            new SurrealFunctionParameter { Name = "a", Type = "int" },
            new SurrealFunctionParameter { Name = "b", Type = "int" });

        options.Functions.Functions.Count.ShouldBe(1);
        var fn = options.Functions.Functions[0];
        fn.Name.ShouldBe("fn::add");
        fn.Body.ShouldBe("RETURN $a + $b");
        fn.ParametersTyped.Count.ShouldBe(2);
        fn.ParametersTyped[0].Name.ShouldBe("a");
        fn.ParametersTyped[0].Type.ShouldBe("int");
        fn.ParametersTyped[1].Name.ShouldBe("b");
        fn.ParametersTyped[1].Type.ShouldBe("int");
    }

    [Test]
    public void DocumentPolicies_ForAllDocuments_AppliesGlobalPolicy()
    {
        var options = new StoreOptions();

        options.Policies.ForAllDocuments(m => { });

        options.Policies.RegisteredPolicies.Count.ShouldBe(1);

        // Verify the policy applies to any mapping
        var mapping = options.Schema.For<Person>();
        options.Policies.RegisteredPolicies[0].Apply(mapping);
        // Should not throw — the policy was a no-op but was called successfully
    }

    [Test]
    public void DocumentPolicies_ForDocumentsOfType_AppliesTypedPolicy()
    {
        var options = new StoreOptions();
        var appliedType = typeof(object);

        options.Policies.ForDocumentsOfType<Person>(m => appliedType = m.DocumentType);

        options.Policies.RegisteredPolicies.Count.ShouldBe(1);

        // Apply to a Person mapping — should invoke the lambda
        var personMapping = options.Schema.For<Person>();
        options.Policies.RegisteredPolicies[0].Apply(personMapping);
        appliedType.ShouldBe(typeof(Person));

        // Apply to a Product mapping — should NOT invoke the lambda (wrong type)
        appliedType = null!;
        var productMapping = options.Schema.For<Product>();
        options.Policies.RegisteredPolicies[0].Apply(productMapping);
        appliedType.ShouldBeNull();
    }

    [Test]
    public void DocumentPolicies_AddPolicy_AddsCustomPolicy()
    {
        var options = new StoreOptions();
        var policy = new FakePolicy();

        options.Policies.AddPolicy(policy);

        options.Policies.RegisteredPolicies.Count.ShouldBe(1);
        options.Policies.RegisteredPolicies[0].ShouldBeSameAs(policy);

        // Apply it to verify custom behavior
        var mapping = options.Schema.For<Person>();
        options.Policies.RegisteredPolicies[0].Apply(mapping);
        policy.Applied.ShouldBeTrue();
    }

    // ──────────────────────────────────────────────
    // Part 2: BulkOperations Extension Methods
    // ──────────────────────────────────────────────

    [Test]
    public async Task BulkInsertAsync_DelegatesToSession()
    {
        var session = Substitute.For<IDocumentSession>();
        var entities = new List<Person>
        {
            new() { Name = "Alice" },
            new() { Name = "Bob" },
            new() { Name = "Charlie" }
        };

        var count = await BulkOperations.BulkInsertAsync(session, entities, batchSize: 100);

        count.ShouldBe(3);
        await session.Received(1).ExecuteSqlAsync(
            Arg.Is<string>(s => s.StartsWith("INSERT INTO person")),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BulkInsertAsync_EmptyList_ReturnsZero()
    {
        var session = Substitute.For<IDocumentSession>();
        var entities = new List<Person>();

        var count = await BulkOperations.BulkInsertAsync(session, entities);

        count.ShouldBe(0);
        await session.DidNotReceiveWithAnyArgs().ExecuteSqlAsync(
            default!, default!, default);
    }

    [Test]
    public async Task BulkDeleteAsync_DelegatesToSession()
    {
        var session = Substitute.For<IDocumentSession>();
        session.LoadAsync<Person>(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new Person { Name = "Found" });

        var ids = new List<string> { "id1", "id2" };

        var count = await BulkOperations.BulkDeleteAsync<Person>(session, ids, batchSize: 100);

        count.ShouldBe(2);
        await session.Received(2).LoadAsync<Person>(Arg.Any<string>(), Arg.Any<CancellationToken>());
        session.Received(2).Delete<Person>(Arg.Any<Person>());
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BulkDeleteAsync_EmptyList_ReturnsZero()
    {
        var session = Substitute.For<IDocumentSession>();
        var ids = new List<string>();

        var count = await BulkOperations.BulkDeleteAsync<Person>(session, ids);

        count.ShouldBe(0);
        await session.DidNotReceiveWithAnyArgs().LoadAsync<Person>(default(string)!, default);
        session.DidNotReceiveWithAnyArgs().Delete<Person>(Arg.Any<Person>());
        await session.DidNotReceiveWithAnyArgs().SaveChangesAsync(default!);
    }
}
