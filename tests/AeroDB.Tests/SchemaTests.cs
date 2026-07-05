using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

public class SchemaTests
{
    [Test]
    public async Task Schema_does_not_fail_on_recreate()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var schemaManager = new SchemaManager();
        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;

        // First creation should succeed
        await schemaManager.EnsureDocumentSchemaAsync<Person>(surrealSession);

        // Second creation (idempotent) should also succeed
        await schemaManager.EnsureDocumentSchemaAsync<Person>(surrealSession);
    }

    [Test]
    public async Task Ensure_event_schema_creates_mt_events()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var schemaManager = new SchemaManager();
        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;

        await schemaManager.EnsureEventSchemaAsync(surrealSession);

        // Verify the table exists by querying INFO FOR TABLE
        var response = await surrealSession.RawQuery("INFO FOR TABLE mt_events;");
        response.HasErrors.ShouldBeFalse();
        response.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Ensure_document_schema_creates_table()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var schemaManager = new SchemaManager();
        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;

        // Ensure schema for Person (should create person table)
        await schemaManager.EnsureDocumentSchemaAsync<TenantPerson>(surrealSession);

        // Verify the table exists
        var response = await surrealSession.RawQuery("INFO FOR TABLE tenant_person;");
        response.HasErrors.ShouldBeFalse();
        response.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Event_schema_auto_created_when_events_enabled()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Events.Enabled = true;
        });
        await store.InitializeAsync();

        // Verify mt_events table was auto-created during InitializeAsync
        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;
        var response = await surrealSession.RawQuery("INFO FOR TABLE mt_events;");
        response.HasErrors.ShouldBeFalse();
        response.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Schema_fields_match_model_properties()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var schemaManager = new SchemaManager();
        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;

        await schemaManager.EnsureDocumentSchemaAsync<Product>(surrealSession);

        // Verify the field definitions exist on the product table
        var response = await surrealSession.RawQuery("INFO FOR TABLE product;");
        response.HasErrors.ShouldBeFalse();
    }
}
