using System.Reflection;
using System.Linq.Expressions;
using AeroDB.Sable;
using Shouldly;
using TUnit.Core;

namespace AeroDB.Tests;

public enum SchemaTestStatus { Draft = 0, Published = 1, Archived = 2 }

public class SchemaEnumDoc
{
    public long Id { get; set; }
    public SchemaTestStatus Status { get; set; }
    public string? CreatedBy { get; set; }
}

public class EnumSchemaForTests
{
    // ── Unit tests: Schema.For<T>() mapping and serialization ──

    [Test]
    public void SchemaFor_creates_mapping_for_enum_doc_type()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<SchemaEnumDoc>();
        mapping.ShouldNotBeNull();
        mapping.EntityType.ShouldBe(typeof(SchemaEnumDoc));
    }

    [Test]
    public void SchemaFor_enum_doc_mapping_is_idempotent()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var m1 = options.Schema.For<SchemaEnumDoc>();
        var m2 = options.Schema.For<SchemaEnumDoc>();
        m1.ShouldBeSameAs(m2);
    }

    [Test]
    public void SchemaFor_set_operation_formats_enum_as_string()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        options.Schema.For<SchemaEnumDoc>();
        var op = new SetOperation("status", SchemaTestStatus.Published, OperationKind.Set,
            enumStorage: options.EnumStorage);
        var result = op.ToSurrealQL();
        result.ShouldContain("'Published'");
    }

    [Test]
    public void SchemaFor_set_operation_formats_enum_as_integer()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        options.Schema.For<SchemaEnumDoc>();
        options.UseSystemTextJsonForSerialization(EnumStorage.AsInteger);
        var op = new SetOperation("status", SchemaTestStatus.Published, OperationKind.Set,
            enumStorage: options.EnumStorage);
        var result = op.ToSurrealQL();
        result.ShouldContain("= 1");
    }

    [Test]
    public void SchemaFor_expression_visitor_formats_enum_as_string()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        options.Schema.For<SchemaEnumDoc>();
        var visitor = new SurrealExpressionVisitor(options.Schema, options.EnumStorage);
        var method = typeof(SurrealExpressionVisitor).GetMethod("FormatValue",
            BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(object)], null);
        var result = method!.Invoke(visitor, [SchemaTestStatus.Published]);
        result.ShouldBe("'Published'");
    }

    [Test]
    public void SchemaFor_expression_visitor_formats_enum_as_integer()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        options.Schema.For<SchemaEnumDoc>();
        options.UseSystemTextJsonForSerialization(EnumStorage.AsInteger);
        var visitor = new SurrealExpressionVisitor(options.Schema, options.EnumStorage);
        var method = typeof(SurrealExpressionVisitor).GetMethod("FormatValue",
            BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(object)], null);
        var result = method!.Invoke(visitor, [SchemaTestStatus.Published]);
        result.ShouldBe("1");
    }

    [Test]
    public void SchemaFor_expression_visitor_formats_inline_enum_comparison_as_string()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        options.Schema.For<SchemaEnumDoc>();
        Expression<Func<SchemaEnumDoc, bool>> predicate =
            document => document.Status == SchemaTestStatus.Published;
        var query = Array.Empty<SchemaEnumDoc>().AsQueryable().Where(predicate);

        var translated = new SurrealExpressionVisitor(options.Schema, options.EnumStorage)
            .Translate(query.Expression);

        translated.Parameters.Values.ShouldHaveSingleItem().ShouldBe("Published");
    }

    [Test]
    public void SchemaFor_expression_visitor_formats_inline_enum_comparison_as_integer()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        options.Schema.For<SchemaEnumDoc>();
        options.UseSystemTextJsonForSerialization(EnumStorage.AsInteger);
        Expression<Func<SchemaEnumDoc, bool>> predicate =
            document => document.Status == SchemaTestStatus.Published;
        var query = Array.Empty<SchemaEnumDoc>().AsQueryable().Where(predicate);

        var translated = new SurrealExpressionVisitor(options.Schema, options.EnumStorage)
            .Translate(query.Expression);

        translated.Parameters.Values.ShouldHaveSingleItem().ShouldBe(1L);
    }

    // ── Integration tests with embedded in-memory SurrealDB ──
    //
    // After the fix, enum properties produce literal type constraints
    // (e.g. TYPE "Draft" | "Published" | "Archived") instead of TYPE object.
    // These tests verify the full save → load round-trip works correctly.

    [Test]
    public async Task SchemaFor_integration_save_and_load_enum_with_strict_schema()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SchemaEnumDoc>()
                .Identity(x => x.Id)
                .SetSchemaMode(SchemaMode.Strict);
        });

        var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.IdentityOnly });

        var doc = new SchemaEnumDoc
        {
            Id = 1,
            Status = SchemaTestStatus.Published,
            CreatedBy = null
        };
        session.Store(doc);
        await session.SaveChangesAsync();

        var loaded = await session.LoadAsync<SchemaEnumDoc>(1);
        loaded.ShouldNotBeNull();
        loaded!.Status.ShouldBe(SchemaTestStatus.Published);
        loaded.CreatedBy.ShouldBeNull();

        var queried = await session.Query<SchemaEnumDoc>()
            .Where(item => item.Status == SchemaTestStatus.Published)
            .ToListAsync();
        queried.Select(item => item.Id).ShouldContain(1);
    }

    [Test]
    public async Task SchemaFor_integration_save_and_load_enum_with_flexible_schema()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SchemaEnumDoc>()
                .Identity(x => x.Id)
                .SetSchemaMode(SchemaMode.Flexible);
        });

        var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.IdentityOnly });

        var doc = new SchemaEnumDoc
        {
            Id = 2,
            Status = SchemaTestStatus.Archived,
            CreatedBy = "test-user"
        };
        session.Store(doc);
        await session.SaveChangesAsync();

        var loaded = await session.LoadAsync<SchemaEnumDoc>(2);
        loaded.ShouldNotBeNull();
        loaded!.Status.ShouldBe(SchemaTestStatus.Archived);
        loaded.CreatedBy.ShouldBe("test-user");
    }

    [Test]
    public async Task SchemaFor_integration_save_and_load_enum_as_integer()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.UseSystemTextJsonForSerialization(EnumStorage.AsInteger);
            o.Schema.For<SchemaEnumDoc>()
                .Identity(x => x.Id)
                .SetSchemaMode(SchemaMode.Strict);
        });

        var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.IdentityOnly });

        var doc = new SchemaEnumDoc
        {
            Id = 3,
            Status = SchemaTestStatus.Published,
            CreatedBy = null
        };
        session.Store(doc);
        await session.SaveChangesAsync();

        var loaded = await session.LoadAsync<SchemaEnumDoc>(3);
        loaded.ShouldNotBeNull();
        loaded!.Status.ShouldBe(SchemaTestStatus.Published);
        loaded.CreatedBy.ShouldBeNull();

        var queried = await session.Query<SchemaEnumDoc>()
            .Where(item => item.Status == SchemaTestStatus.Published)
            .ToListAsync();
        queried.Select(item => item.Id).ShouldContain(3);
    }
}
