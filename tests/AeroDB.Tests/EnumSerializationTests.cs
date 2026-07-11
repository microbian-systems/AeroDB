using System.Reflection;
using AeroDB.Sable;
using Shouldly;
using TUnit.Core;

namespace AeroDB.Tests;

public class EnumSerializationTests
{
    [Test]
    public void ExpressionVisitor_FormatValue_AsString_emits_string_name()
    {
        var visitor = new SurrealExpressionVisitor(null, EnumStorage.AsString);
        var method = typeof(SurrealExpressionVisitor).GetMethod("FormatValue",
            BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(object)], null);
        var result = method!.Invoke(visitor, [TestStatus.Published]);
        result.ShouldBe("'Published'");
    }

    [Test]
    public void ExpressionVisitor_FormatValue_AsInteger_emits_integer()
    {
        var visitor = new SurrealExpressionVisitor(null, EnumStorage.AsInteger);
        var method = typeof(SurrealExpressionVisitor).GetMethod("FormatValue",
            BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(object)], null);
        var result = method!.Invoke(visitor, [TestStatus.Published]);
        result.ShouldBe("1");
    }

    [Test]
    public void SetOperation_FormatValue_AsString_emits_string_name()
    {
        var op = new SetOperation("status", TestStatus.Published, OperationKind.Set, enumStorage: EnumStorage.AsString);
        var result = op.ToSurrealQL();
        result.ShouldContain("'Published'");
    }

    [Test]
    public void SetOperation_FormatValue_AsInteger_emits_integer()
    {
        var op = new SetOperation("status", TestStatus.Published, OperationKind.Set, enumStorage: EnumStorage.AsInteger);
        var result = op.ToSurrealQL();
        result.ShouldContain("= 1");
    }

    [Test]
    public void StoreOptions_default_EnumStorage_is_AsString()
    {
        var options = new StoreOptions();
        options.EnumStorage.ShouldBe(EnumStorage.AsString);
    }

    [Test]
    public void UseSystemTextJsonForSerialization_sets_EnumStorage_on_options()
    {
        var options = new StoreOptions();
        options.UseSystemTextJsonForSerialization(EnumStorage.AsInteger);
        options.EnumStorage.ShouldBe(EnumStorage.AsInteger);
    }

    [Test]
    public void SetOperation_FormatValue_null_emits_NONE()
    {
        var op = new SetOperation("status", null, OperationKind.Set);
        var result = op.ToSurrealQL();
        result.ShouldContain("NONE");
    }

    [Test]
    public void SetOperation_FormatValue_string_emits_quoted()
    {
        var op = new SetOperation("name", "hello", OperationKind.Set);
        var result = op.ToSurrealQL();
        result.ShouldContain("'hello'");
    }

    [Test]
    public void SetOperation_FormatValue_int_emits_number()
    {
        var op = new SetOperation("count", 42, OperationKind.Set);
        var result = op.ToSurrealQL();
        result.ShouldContain("= 42");
    }
}
