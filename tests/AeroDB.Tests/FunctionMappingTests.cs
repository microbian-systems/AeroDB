using System.Linq.Expressions;
using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Test model for function mapping (Math + string interception) verification.
/// </summary>
public class FunctionTestDoc
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public double OtherValue { get; set; }
    public string[] Tags { get; set; } = [];
}


/// <summary>
/// Verifies that <see cref="SurrealExpressionVisitor"/> correctly translates
/// .NET method calls (Math, string) into SurrealQL function expressions.
///
/// Tests build expression trees manually and check the SurrealQL output via
/// <see cref="SurrealExpressionVisitor.TranslateCondition"/> or
/// <see cref="SurrealExpressionVisitor.Translate"/>.
/// </summary>
public partial class FunctionMappingTests
{
    // ════════════════════════════════════════════════════════════
    // Unsupported methods
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Math_Acos_ThrowsNotSupportedException()
    {
        // x => Math.Acos(x.Value) > 0
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var acosCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Acos), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(acosCall, Expression.Constant(0.0));

        var ex = Should.Throw<NotSupportedException>(() =>
            SurrealExpressionVisitor.TranslateCondition(condition));

        ex.Message.ShouldContain("Acos");
    }

    // ════════════════════════════════════════════════════════════
    // Full query integration — Phase 5b in WHERE
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task StringReplace_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var replaceCall = Expression.Call(nameProp,
            typeof(string).GetMethod("Replace", [typeof(string), typeof(string)])!,
            Expression.Constant("a"), Expression.Constant("b"));
        var condition = Expression.Equal(replaceCall, Expression.Constant("hello"));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::replace");
    }

    [Test]
    public async Task SurrealStringFunctions_IsEmail_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var isEmailCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsEmail", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(isEmailCall, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::is::email");
    }

    [Test]
    public async Task SurrealStringFunctions_IsUrl_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var isUrlCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsUrl", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(isUrlCall, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::is::url");
    }

    [Test]
    public async Task SurrealStringFunctions_IsUuid_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var isUuidCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsUuid", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(isUuidCall, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::is::uuid");
    }

    [Test]
    public async Task SurrealStringFunctions_IsNumeric_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var isNumericCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsNumeric", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(isNumericCall, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::is::numeric");
    }

    [Test]
    public async Task SurrealStringFunctions_IsDatetime_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var isDatetimeCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsDatetime", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(isDatetimeCall, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::is::datetime");
    }

    [Test]
    public async Task SurrealStringFunctions_IsAlphanum_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsAlphanum", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::is::alphanum");
    }

    [Test]
    public async Task SurrealStringFunctions_IsAlpha_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsAlpha", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::is::alpha");
    }

    [Test]
    public async Task SurrealStringFunctions_IsAscii_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsAscii", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::is::ascii");
    }

    [Test]
    public async Task Join_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var joinCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("Join", [typeof(string), typeof(string[])])!,
            Expression.Constant(","),
            Expression.NewArrayInit(typeof(string), nameProp, Expression.Constant("end")));
        var condition = Expression.Equal(joinCall, Expression.Constant("value,end"));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::join");
    }

    [Test]
    public async Task SimilarityFuzzy_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var fuzzyCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("SimilarityFuzzy",
                [typeof(string), typeof(string)])!,
            nameProp, Expression.Constant("target"));
        var condition = Expression.GreaterThan(fuzzyCall, Expression.Constant(0));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::similarity::fuzzy");
    }

    [Test]
    public async Task DistanceLevenshtein_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var levCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("DistanceLevenshtein",
                [typeof(string), typeof(string)])!,
            nameProp, Expression.Constant("target"));
        var condition = Expression.GreaterThan(levCall, Expression.Constant(0));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::distance::levenshtein");
    }

}
