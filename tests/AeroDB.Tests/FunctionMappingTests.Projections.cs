using System.Linq.Expressions;
using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

public partial class FunctionMappingTests
{
    // ════════════════════════════════════════════════════════════
    // Phase 7a: Aggregate functions with method calls
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Aggregate_Sum_MathAbs_ProducesNestedFunction()
    {
        var visitor = new SurrealExpressionVisitor();
        var queryable = Array.Empty<FunctionTestDoc>().AsQueryable();
        Expression<Func<FunctionTestDoc, double>> expr = x => Math.Abs(x.Value);

        var sumExpr = Expression.Call(
            typeof(Queryable), "Sum",
            [typeof(FunctionTestDoc)],
            queryable.Expression, Expression.Quote(expr));

        var result = visitor.Translate(sumExpr);
        result.Projection.ShouldBe("math::sum(math::abs(value))");
    }

    [Test]
    public async Task Aggregate_Min_MathRound_ProducesNestedFunction()
    {
        var visitor = new SurrealExpressionVisitor();
        var queryable = Array.Empty<FunctionTestDoc>().AsQueryable();
        Expression<Func<FunctionTestDoc, double>> expr = x => Math.Round(x.Value);

        var minExpr = Expression.Call(
            typeof(Queryable), "Min",
            [typeof(FunctionTestDoc), typeof(double)],
            queryable.Expression, Expression.Quote(expr));

        var result = visitor.Translate(minExpr);
        result.Projection.ShouldBe("math::min(math::round(value))");
    }

    [Test]
    public async Task Aggregate_Max_MathSqrt_ProducesNestedFunction()
    {
        var visitor = new SurrealExpressionVisitor();
        var queryable = Array.Empty<FunctionTestDoc>().AsQueryable();
        Expression<Func<FunctionTestDoc, double>> expr = x => Math.Sqrt(x.Value);

        var maxExpr = Expression.Call(
            typeof(Queryable), "Max",
            [typeof(FunctionTestDoc), typeof(double)],
            queryable.Expression, Expression.Quote(expr));

        var result = visitor.Translate(maxExpr);
        result.Projection.ShouldBe("math::max(math::sqrt(value))");
    }

    [Test]
    public async Task Aggregate_Avg_MathAbs_ProducesNestedFunction()
    {
        var visitor = new SurrealExpressionVisitor();
        var queryable = Array.Empty<FunctionTestDoc>().AsQueryable();
        Expression<Func<FunctionTestDoc, double>> expr = x => Math.Abs(x.Value);

        var avgExpr = Expression.Call(
            typeof(Queryable), "Average",
            [typeof(FunctionTestDoc)],
            queryable.Expression, Expression.Quote(expr));

        var result = visitor.Translate(avgExpr);
        result.Projection.ShouldBe("math::mean(math::abs(value))");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 6: Projection (SELECT) with method calls
    // ════════════════════════════════════════════════════════════

    private static Expression BuildSelectExpr<TSource, TProjection>(
        Expression<Func<TSource, TProjection>> selector,
        Expression source)
    {
        return Expression.Call(typeof(Queryable), "Select",
            [typeof(TSource), typeof(TProjection)],
            source, Expression.Quote(selector));
    }

    [Test]
    public async Task Select_MathAbs_ProducesCorrectProjection()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var expression = BuildSelectExpr<FunctionTestDoc, FunctionTestDoc>(
            x => new FunctionTestDoc { Value = Math.Abs(x.Value) },
            source.Expression);

        var result = visitor.Translate(expression);
        result.Projection.ShouldContain("math::abs(value) AS Value");
    }

    [Test]
    public async Task Select_StringTrim_ProducesCorrectProjection()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var expression = BuildSelectExpr<FunctionTestDoc, FunctionTestDoc>(
            x => new FunctionTestDoc { Name = x.Name.Trim() },
            source.Expression);

        var result = visitor.Translate(expression);
        result.Projection.ShouldContain("string::trim(name) AS Name");
    }

    [Test]
    public async Task Select_TypeOf_ProducesCorrectProjection()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var expression = BuildSelectExpr<FunctionTestDoc, FunctionTestDoc>(
            x => new FunctionTestDoc { Name = SurrealTypeFunctions.TypeOf(x.Name) },
            source.Expression);

        var result = visitor.Translate(expression);
        result.Projection.ShouldContain("type::of(name) AS Name");
    }

    [Test]
    public async Task Select_ArrayLen_ProducesCorrectProjection()
    {
        // Build: source.Select(x => new FunctionTestDoc { Value = SurrealArrayFunctions.Len(x.Tags) })
        // Use manual expression tree to avoid compiler issues with generic methods in lambdas
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var selectMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Select" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc), typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var tagsProp = Expression.Property(param, "Tags");
        var lenMethod = typeof(SurrealArrayFunctions).GetMethod("Len")!.MakeGenericMethod(typeof(string));
        var lenCall = Expression.Call(lenMethod, tagsProp);

        var valueProp = typeof(FunctionTestDoc).GetProperty("Value")!;
        var bind = Expression.Bind(valueProp, Expression.Convert(lenCall, typeof(double)));
        var init = Expression.MemberInit(Expression.New(typeof(FunctionTestDoc)), bind);

        var lambda = Expression.Lambda<Func<FunctionTestDoc, FunctionTestDoc>>(init, param);

        var expression = Expression.Call(selectMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Projection.ShouldContain("array::len(tags) AS Value");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 6: ORDER BY with method calls
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task OrderBy_MathAbs_ProducesCorrectOrderBy()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var orderByMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "OrderBy" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc), typeof(double));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var absCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Abs), [typeof(double)])!,
            valueProp);
        var lambda = Expression.Lambda<Func<FunctionTestDoc, double>>(absCall, param);

        var expression = Expression.Call(orderByMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.OrderBy.Count.ShouldBe(1);
        result.OrderBy[0].ShouldBe("math::abs(value) ASC");
    }

    [Test]
    public async Task OrderByDescending_StringLength_ProducesCorrectOrderBy()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var orderByMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "OrderByDescending" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc), typeof(int));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var lengthProp = Expression.Property(nameProp, "Length");
        var lambda = Expression.Lambda<Func<FunctionTestDoc, int>>(lengthProp, param);

        var expression = Expression.Call(orderByMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.OrderBy.Count.ShouldBe(1);
        result.OrderBy[0].ShouldBe("string::length(name) DESC");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 6: GROUP BY with method calls
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task GroupBy_TypeOf_ProducesCorrectGroupBy()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var groupByMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "GroupBy" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc), typeof(string));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var typeOfCall = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeOf", [typeof(object)])!,
            Expression.Convert(nameProp, typeof(object)));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, string>>(typeOfCall, param);

        var expression = Expression.Call(groupByMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.GroupBy.Count.ShouldBe(1);
        result.GroupBy[0].ShouldBe("type::of(name)");
    }
}
