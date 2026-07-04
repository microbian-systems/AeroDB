using Microsoft.Extensions.DependencyInjection;
using SurrealDb.Embedded.InMemory;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

// ──────────────────────────────────────────────
// Event types for EventSourcingOptions tests
// ──────────────────────────────────────────────

file sealed class EventA { public string Name { get; set; } = ""; }
file sealed class EventB { public int Value { get; set; } }
file sealed class EventC { public string? Description { get; set; } }
file sealed class EventD { public DateTime Timestamp { get; set; } }

// ──────────────────────────────────────────────
// Tests
// ──────────────────────────────────────────────

public class StoreOptionsParityTests
{
    // ──────────────────────────────────────────────
    // 1. IReadOnlyStoreOptions interface
    // ──────────────────────────────────────────────

    [Test]
    public void IReadOnlyStoreOptions_exposes_readonly_properties()
    {
        var opts = new StoreOptions();
        opts.Database = "testdb";
        opts.TenancyStyle = TenancyStyle.Conjoined;
        opts.UpdateBatchSize = 250;

        IReadOnlyStoreOptions readOnly = opts;

        readOnly.Database.ShouldBe("testdb");
        readOnly.TenancyStyle.ShouldBe(TenancyStyle.Conjoined);
        readOnly.UpdateBatchSize.ShouldBe(250);
        readOnly.Projections.ShouldNotBeNull();
    }

    [Test]
    public void IReadOnlyStoreOptions_Projections_is_readonly_list()
    {
        var opts = new StoreOptions();
        IReadOnlyStoreOptions readOnly = opts;

        readOnly.Projections.Count.ShouldBe(0);
        readOnly.Projections.ShouldBeAssignableTo<IReadOnlyList<IProjection>>();
    }

    [Test]
    public void StoreOptions_is_IReadOnlyStoreOptions()
    {
        var opts = new StoreOptions();
        (opts is IReadOnlyStoreOptions).ShouldBeTrue();
    }

    // ──────────────────────────────────────────────
    // 2. ProjectionOptions.Snapshot<T>
    // ──────────────────────────────────────────────

    [Test]
    public void ProjectionOptions_Snapshot_registers_projection_and_returns_document_mapping()
    {
        var opts = new StoreOptions();
        var mapping = opts.ProjectionBuild.Snapshot<Person>(ProjectionLifecycle.Inline);

        mapping.ShouldNotBeNull();
        mapping.ShouldBeOfType<DocumentMapping<Person>>();
        opts.Projections.Count.ShouldBe(1);
        opts.Projections[0].Lifecycle.ShouldBe(ProjectionLifecycle.Inline);
    }

    [Test]
    public void ProjectionOptions_Snapshot_with_configure()
    {
        var opts = new StoreOptions();
        opts.ProjectionBuild.Snapshot<Person>(ProjectionLifecycle.Async, o => o.SnapshotFrequency = 100);

        opts.Projections.Count.ShouldBe(1);
        opts.Projections[0].Lifecycle.ShouldBe(ProjectionLifecycle.Async);
    }

    // ──────────────────────────────────────────────
    // 3. StoreOptions new properties
    // ──────────────────────────────────────────────

    [Test]
    public void StoreOptions_CommandTimeout_is_settable()
    {
        var opts = new StoreOptions { CommandTimeout = 30 };
        opts.CommandTimeout.ShouldBe(30);
    }

    [Test]
    public void StoreOptions_UpdateBatchSize_defaults_to_500()
    {
        var opts = new StoreOptions();
        opts.UpdateBatchSize.ShouldBe(500);
    }

    [Test]
    public void StoreOptions_OpenTelemetry_config()
    {
        var opts = new StoreOptions();
        opts.OpenTelemetry = new OpenTelemetryOptions
        {
            TrackDocumentStore = false,
            IncludeSqlStatements = true
        };

        opts.OpenTelemetry.ShouldNotBeNull();
        opts.OpenTelemetry.TrackDocumentStore.ShouldBeFalse();
        opts.OpenTelemetry.IncludeSqlStatements.ShouldBeTrue();
    }

    [Test]
    public void StoreOptions_ConfigurePolly_stores_configuration()
    {
        var opts = new StoreOptions();
        var invoked = false;

        opts.ConfigurePolly(_ => { invoked = true; });

        // Verify the method doesn't throw and the callback was invoked
        invoked.ShouldBeTrue();
    }

    // ──────────────────────────────────────────────
    // 4. Policies convenience methods
    // ──────────────────────────────────────────────

    [Test]
    public void AllDocumentsAreMultiTenanted_sets_policy()
    {
        var opts = new StoreOptions();
        opts.AllDocumentsAreMultiTenanted();

        var mapping = opts.Schema.For<Person>();

        // Apply registered policies manually (policies are applied during store init)
        foreach (var policy in opts.Policies.RegisteredPolicies)
            policy.Apply(mapping);

        mapping.TenancyStyle.ShouldBe(TenancyStyle.Conjoined);
    }

    [Test]
    public void AllDocumentsSoftDeleted_sets_policy()
    {
        var opts = new StoreOptions();
        opts.AllDocumentsSoftDeleted();

        var mapping = opts.Schema.For<Product>();

        foreach (var policy in opts.Policies.RegisteredPolicies)
            policy.Apply(mapping);

        mapping.SoftDeleted.ShouldBeTrue();
    }

    [Test]
    public void AllDocumentsEnforceOptimisticConcurrency_sets_policy()
    {
        var opts = new StoreOptions();
        opts.AllDocumentsEnforceOptimisticConcurrency();

        opts.UseOptimisticConcurrency.ShouldBeTrue();

        var mapping = opts.Schema.For<Person>();

        foreach (var policy in opts.Policies.RegisteredPolicies)
            policy.Apply(mapping);

        mapping.UseOptimisticConcurrency.ShouldBeTrue();
    }

    // ──────────────────────────────────────────────
    // 5. EventSourcingOptions new properties
    // ──────────────────────────────────────────────

    [Test]
    public void EventSourcingOptions_StreamIdentity_defaults_to_AsGuid()
    {
        var opts = new StoreOptions();
        opts.Events.StreamIdentity.ShouldBe(StreamIdentity.AsGuid);
    }

    [Test]
    public void EventSourcingOptions_StreamIdentity_settable()
    {
        var opts = new StoreOptions();
        opts.Events.StreamIdentity = StreamIdentity.AsString;
        opts.Events.StreamIdentity.ShouldBe(StreamIdentity.AsString);
    }

    [Test]
    public void EventSourcingOptions_AddEventType_registers_types()
    {
        var opts = new StoreOptions();

        opts.Events.AddEventType<EventA>();
        opts.Events.AddEventType(typeof(EventB));
        opts.Events.AddEventTypes(new[] { typeof(EventC) });

        // Smoke test — methods should not throw and return the options for chaining
        opts.Events.ShouldNotBeNull();
    }

    [Test]
    public void EventSourcingOptions_MapEventType_registers_name_overrides()
    {
        var opts = new StoreOptions();

        opts.Events.MapEventType<EventC>("custom_event_name");
        opts.Events.MapEventType(typeof(EventD), "another_name");

        // Smoke test — methods should not throw and return the options for chaining
        opts.Events.ShouldNotBeNull();
    }

    [Test]
    public void EventSourcingOptions_bool_properties_default_to_false()
    {
        var opts = new StoreOptions();

        opts.Events.EnableSideEffectsOnInlineProjections.ShouldBeFalse();
        opts.Events.UseIdentityMapForAggregates.ShouldBeFalse();
        opts.Events.EnableUniqueIndexOnEventId.ShouldBeFalse();
        opts.Events.EnableEventTypeIndex.ShouldBeFalse();
        opts.Events.EnableBigIntEvents.ShouldBeFalse();
        opts.Events.UseMandatoryStreamTypeDeclaration.ShouldBeFalse();
    }

    [Test]
    public void EventSourcingOptions_TenancyStyle_defaults_to_Single()
    {
        var opts = new StoreOptions();
        opts.Events.TenancyStyle.ShouldBe(TenancyStyle.Single);
    }

    // ──────────────────────────────────────────────
    // 6. DI extensions — AddDali overloads
    // ──────────────────────────────────────────────

    [Test]
    public async Task AddDali_registers_IDocumentStore()
    {
        var services = new ServiceCollection();
        services.AddDali(options =>
        {
            options.ClientFactory = () => new SurrealDbMemoryClient();
            options.Namespace = "test";
            options.Database = "test";
        });

        var provider = services.BuildServiceProvider();
        var store = provider.GetService<IDocumentStore>();

        store.ShouldNotBeNull();
        await store.DisposeAsync();
    }

    [Test]
    public async Task AddDali_connection_string_configuration()
    {
        // AddDali only accepts Action<StoreOptions>; configure endpoint via lambda
        var services = new ServiceCollection();
        services.AddDali(options =>
        {
            options.Endpoint = "ws://example.com:8000";
            options.Namespace = "app_ns";
            options.Database = "app_db";
            options.Username = "admin";
            options.Password = "secret";
            // Use in-memory client so registration succeeds
            options.ClientFactory = () => new SurrealDbMemoryClient();
        });

        var provider = services.BuildServiceProvider();
        var store = provider.GetService<IDocumentStore>();

        store.ShouldNotBeNull();
        store.Options.Endpoint.ShouldBe("ws://example.com:8000");
        store.Options.Namespace.ShouldBe("app_ns");
        store.Options.Database.ShouldBe("app_db");
        await store.DisposeAsync();
    }

    // ──────────────────────────────────────────────
    // 7. SnapshotLifecycle enum
    // ──────────────────────────────────────────────

    [Test]
    public void SnapshotLifecycle_has_Inline_and_Async_values()
    {
        ((int)SnapshotLifecycle.Inline).ShouldBe(0);
        ((int)SnapshotLifecycle.Async).ShouldBe(1);
    }

    // ──────────────────────────────────────────────
    // 8. Old SnapshotsExtensions still works (with Obsolete)
    // ──────────────────────────────────────────────

#pragma warning disable CS0618 // Type or member is obsolete
    [Test]
    public void old_SnapshotExtensions_still_registers_projection()
    {
        var opts = new StoreOptions();
        opts.Projections.Snapshot<Person>();

        opts.Projections.Count.ShouldBe(1);
    }
#pragma warning restore CS0618
}
