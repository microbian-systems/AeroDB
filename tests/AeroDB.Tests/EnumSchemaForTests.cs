using System.Reflection;
using AeroDB.Sable;
using Shouldly;
using TUnit.Core;

namespace AeroDB.Tests;

public enum SchemaTestStatus { Draft = 0, Published = 1, Archived = 2 }

public class SchemaEnumDoc
{
    public SchemaTestStatus Status { get; set; }
}

public class EnumSchemaForTests
{
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
}
