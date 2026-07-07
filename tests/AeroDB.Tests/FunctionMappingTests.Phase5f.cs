using System.Linq.Expressions;
using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

public partial class FunctionMappingTests
{
    // ════════════════════════════════════════════════════════════
    // Phase 5f: SurrealObjectFunctions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealObjectFunctions_Keys_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var method = typeof(SurrealObjectFunctions).GetMethod("Keys", [typeof(object)])!;
        var call = Expression.Call(method, Expression.Convert(nameProp, typeof(object)));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("object::keys(name) = NONE");
    }

    [Test]
    public async Task SurrealObjectFunctions_Values_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var method = typeof(SurrealObjectFunctions).GetMethod("Values", [typeof(object)])!;
        var call = Expression.Call(method, Expression.Convert(nameProp, typeof(object)));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("object::values(name) = NONE");
    }

    [Test]
    public async Task SurrealObjectFunctions_Len_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var method = typeof(SurrealObjectFunctions).GetMethod("Len", [typeof(object)])!;
        var call = Expression.Call(method, Expression.Convert(nameProp, typeof(object)));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0)));
        result.ShouldBe("object::len(name) > 0");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5f: SurrealHttpFunctions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealHttpFunctions_Get_TranslatesCorrectly()
    {
        var method = typeof(SurrealHttpFunctions).GetMethod("Get", [typeof(string)])!;
        var call = Expression.Call(method, Expression.Constant("https://example.com"));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("r")));
        result.ShouldBe("http::get('https://example.com') = 'r'");
    }

    [Test]
    public async Task SurrealHttpFunctions_Post_TranslatesCorrectly()
    {
        var method = typeof(SurrealHttpFunctions).GetMethod("Post", [typeof(string), typeof(string)])!;
        var call = Expression.Call(method, Expression.Constant("https://example.com"), Expression.Constant("{\"k\":\"v\"}"));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("r")));
        result.ShouldBe("http::post('https://example.com', '{\"k\":\"v\"}') = 'r'");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5f: SurrealBytesFunctions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealBytesFunctions_Len_TranslatesCorrectly()
    {
        var method = typeof(SurrealBytesFunctions).GetMethod("Len", [typeof(byte[])])!;
        var call = Expression.Call(method, Expression.Constant(new byte[] { 1, 2, 3 }));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0)));
        result.ShouldContain("bytes::len(");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5f: SurrealDurationFunctions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealDurationFunctions_Days_TranslatesCorrectly()
    {
        var method = typeof(SurrealDurationFunctions).GetMethod("Days", [typeof(string)])!;
        var call = Expression.Call(method, Expression.Constant("7d"));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0L)));
        result.ShouldBe("duration::days('7d') > 0");
    }

    [Test]
    public async Task SurrealDurationFunctions_Hours_TranslatesCorrectly()
    {
        var method = typeof(SurrealDurationFunctions).GetMethod("Hours", [typeof(string)])!;
        var call = Expression.Call(method, Expression.Constant("24h"));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0L)));
        result.ShouldBe("duration::hours('24h') > 0");
    }

    [Test]
    public async Task SurrealDurationFunctions_Mins_TranslatesCorrectly()
    {
        var method = typeof(SurrealDurationFunctions).GetMethod("Mins", [typeof(string)])!;
        var call = Expression.Call(method, Expression.Constant("30m"));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0L)));
        result.ShouldBe("duration::mins('30m') > 0");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5f: Full query integration
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task RandInt_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var method = typeof(SurrealRandFunctions).GetMethod("Int", [typeof(int), typeof(int)])!;
        var call = Expression.Call(method, Expression.Constant(1), Expression.Constant(100));
        var condition = Expression.GreaterThan(call, Expression.Constant(50));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("rand::int");
    }

    [Test]
    public async Task ObjectKeys_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var method = typeof(SurrealObjectFunctions).GetMethod("Keys", [typeof(object)])!;
        var call = Expression.Call(method, Expression.Convert(nameProp, typeof(object)));
        var condition = Expression.Equal(call, Expression.Constant(null));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("object::keys");
    }

    [Test]
    public async Task HttpGet_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var method = typeof(SurrealHttpFunctions).GetMethod("Get", [typeof(string)])!;
        var call = Expression.Call(method, Expression.Constant("https://api.example.com"));
        var condition = Expression.Equal(call, Expression.Constant("ok"));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("http::get");
    }

    [Test]
    public async Task DurationDays_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var method = typeof(SurrealDurationFunctions).GetMethod("Days", [typeof(string)])!;
        var call = Expression.Call(method, Expression.Constant("7d"));
        var condition = Expression.GreaterThan(call, Expression.Constant(0L));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("duration::days");
    }

}
