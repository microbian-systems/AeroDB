using System.Linq.Expressions;
using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

public partial class FunctionMappingTests
{
    // ════════════════════════════════════════════════════════════
    // Phase 5d: Extended search/vector functions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealFunctions_Highlight_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealFunctions).GetMethod("Highlight", [typeof(string), typeof(string), typeof(string)])!,
            Expression.Constant("<b>"), Expression.Constant("</b>"), nameProp);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("r")));
        result.ShouldBe("search::highlight('<b>', '</b>', name) = 'r'");
    }

    [Test]
    public async Task SurrealFunctions_Offsets_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealFunctions).GetMethod("Offsets", [typeof(string)])!,
            nameProp);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("r")));
        result.ShouldBe("search::offsets(name) = 'r'");
    }

    [Test]
    public async Task SurrealFunctions_Analyze_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealFunctions).GetMethod("Analyze", [typeof(string), typeof(string)])!,
            nameProp, Expression.Constant("english"));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("r")));
        result.ShouldBe("search::analyze(name, 'english') = 'r'");
    }

    [Test]
    public async Task SurrealFunctions_VectorDistanceEuclidean_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealFunctions).GetMethod("VectorDistanceEuclidean", [typeof(float[]), typeof(float[])])!,
            Expression.Constant(new float[] { 1, 2 }), Expression.Constant(new float[] { 3, 4 }));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0.0)));
        result.ShouldContain("vector::distance::euclidean(");
    }

    [Test]
    public async Task SurrealFunctions_VectorDistanceManhattan_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealFunctions).GetMethod("VectorDistanceManhattan", [typeof(float[]), typeof(float[])])!,
            Expression.Constant(new float[] { 1, 2 }), Expression.Constant(new float[] { 3, 4 }));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0.0)));
        result.ShouldContain("vector::distance::manhattan(");
    }

}
