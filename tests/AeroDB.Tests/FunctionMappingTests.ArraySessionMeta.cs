using System.Linq.Expressions;
using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

public partial class FunctionMappingTests
{
    // ════════════════════════════════════════════════════════════
    // Phase 5e: SurrealArrayFunctions — expression-level tests
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealArrayFunctions_Add_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var valConst = Expression.Constant("c");
        var addMethod = typeof(SurrealArrayFunctions).GetMethod("Add")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(addMethod, arrConst, valConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::add(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Append_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var valConst = Expression.Constant("c");
        var method = typeof(SurrealArrayFunctions).GetMethod("Append")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst, valConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::append(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Prepend_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var valConst = Expression.Constant("c");
        var method = typeof(SurrealArrayFunctions).GetMethod("Prepend")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst, valConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::prepend(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Remove_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var method = typeof(SurrealArrayFunctions).GetMethod("Remove")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst, Expression.Constant(0));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::remove(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Sort_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "b", "a" });
        var method = typeof(SurrealArrayFunctions).GetMethod("Sort")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::sort(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Distinct_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b", "a" });
        var method = typeof(SurrealArrayFunctions).GetMethod("Distinct")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::distinct(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Union_TranslatesCorrectly()
    {
        var arr1 = Expression.Constant(new string[] { "a", "b" });
        var arr2 = Expression.Constant(new string[] { "c", "d" });
        var method = typeof(SurrealArrayFunctions).GetMethod("Union")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arr1, arr2);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::union(");
    }

    [Test]
    public async Task SurrealArrayFunctions_ArrayContains_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var valConst = Expression.Constant("a");
        var method = typeof(SurrealArrayFunctions).GetMethod("ArrayContains")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst, valConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldContain("array::contains(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Len_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var method = typeof(SurrealArrayFunctions).GetMethod("Len")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0)));
        result.ShouldContain("array::len(");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5e: SurrealSessionFunctions — expression-level tests
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealSessionFunctions_Id_TranslatesCorrectly()
    {
        var call = Expression.Call(typeof(SurrealSessionFunctions).GetMethod("Id", Type.EmptyTypes)!);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("s")));
        result.ShouldBe("session::id() = 's'");
    }

    [Test]
    public async Task SurrealSessionFunctions_Ip_TranslatesCorrectly()
    {
        var call = Expression.Call(typeof(SurrealSessionFunctions).GetMethod("Ip", Type.EmptyTypes)!);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("s")));
        result.ShouldBe("session::ip() = 's'");
    }

    [Test]
    public async Task SurrealSessionFunctions_Ns_TranslatesCorrectly()
    {
        var call = Expression.Call(typeof(SurrealSessionFunctions).GetMethod("Ns", Type.EmptyTypes)!);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("s")));
        result.ShouldBe("session::ns() = 's'");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5e: SurrealMetaFunctions — expression-level tests
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealMetaFunctions_Id_TranslatesCorrectly()
    {
        var call = Expression.Call(typeof(SurrealMetaFunctions).GetMethod("Id", Type.EmptyTypes)!);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("m")));
        result.ShouldBe("meta::id() = 'm'");
    }

    [Test]
    public async Task SurrealMetaFunctions_Table_TranslatesCorrectly()
    {
        var call = Expression.Call(typeof(SurrealMetaFunctions).GetMethod("Table", Type.EmptyTypes)!);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("m")));
        result.ShouldBe("meta::table() = 'm'");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5e: Full query integration
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task ArrayContains_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var method = typeof(SurrealArrayFunctions).GetMethod("ArrayContains")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(method,
            Expression.Constant(new string[] { "a", "b" }), nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("array::contains");
    }

    [Test]
    public async Task ArrayLen_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var method = typeof(SurrealArrayFunctions).GetMethod("Len")!.MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, Expression.Constant(new string[] { "a", "b", "c" }));
        var condition = Expression.GreaterThan(call, Expression.Constant(0));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("array::len");
    }

    [Test]
    public async Task SessionId_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var call = Expression.Call(typeof(SurrealSessionFunctions).GetMethod("Id", Type.EmptyTypes)!);
        var condition = Expression.Equal(call, Expression.Constant("sess"));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("session::id");
    }

    [Test]
    public async Task MetaId_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var call = Expression.Call(typeof(SurrealMetaFunctions).GetMethod("Id", Type.EmptyTypes)!);
        var condition = Expression.Equal(call, Expression.Constant("meta"));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("meta::id");
    }

}
