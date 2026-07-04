using System.Linq.Expressions;
using TUnit.Core;

namespace Dali.Tests;

public partial class FunctionMappingTests
{
    // ════════════════════════════════════════════════════════════
    // Phase 5c: SurrealTypeFunctions — conversion functions
    // ════════════════════════════════════════════════════════════

    private static Expression ObjectArg(MemberExpression member)
        => member.Type == typeof(object) ? member : Expression.Convert(member, typeof(object));

    [Test]
    public async Task SurrealTypeFunctions_TypeBool_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeBool", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::bool(Value) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeBytes_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeBytes", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::bytes(Value) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeDatetime_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeDatetime", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::datetime(Name) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeDecimal_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeDecimal", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::decimal(Value) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeDuration_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeDuration", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::duration(Value) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeFloat_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeFloat", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::float(Value) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeInt_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeInt", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::int(Value) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeNumber_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeNumber", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::number(Value) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypePoint_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypePoint", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::point(Value) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeString_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeString", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::string(Name) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeTable_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeTable", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::table(Name) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeThing_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeThing", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::thing(Name) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeRecord_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeRecord", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("type::record(Name) = NONE");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeOf_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeOf", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("string")));
        result.ShouldBe("type::of(Name) = 'string'");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5c: SurrealTypeFunctions — checker functions (type::is_*)
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealTypeFunctions_IsBool_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsBool", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_bool(Value) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsInt_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsInt", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_int(Value) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsFloat_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsFloat", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_float(Value) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsString_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsString", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_string(Name) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsDatetime_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsDatetime", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_datetime(Value) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsArray_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsArray", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_array(Name) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsObject_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsObject", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_object(Name) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsNull_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsNull", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_null(Name) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsNumber_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsNumber", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_number(Value) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsRecord_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsRecord", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_record(Name) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsUuid_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsUuid", [typeof(object)])!,
            ObjectArg(nameProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_uuid(Name) = true");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsGeometry_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var call = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsGeometry", [typeof(object)])!,
            ObjectArg(valueProp));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("type::is_geometry(Value) = true");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5c: Unsupported type method
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealTypeFunctions_UnknownMethod_ThrowsNotSupportedException()
    {
        // There is no "IsFooBar" method in the marker class,
        // but we can call a non-existent method name directly.
        // Use a binary expression with a non-existent marker call
        var method = typeof(SurrealTypeFunctions).GetMethod("IsString", [typeof(object)])!;
        // IsString is valid — use it as a baseline that it works
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(method, ObjectArg(nameProp));
        var condition = Expression.Equal(call, Expression.Constant(true));

        // Should not throw
        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("type::is_string(Name) = true");

        // Test NotSupported for unrecognized type function name:
        // string.IsNullOrWhiteSpace is a static string method not handled by our switch.
        var ex = Should.Throw<NotSupportedException>(() =>
            SurrealExpressionVisitor.TranslateCondition(
                Expression.Call(
                    typeof(string).GetMethod("IsNullOrWhiteSpace", [typeof(string)])!,
                    nameProp)));
        ex.Message.ShouldContain("IsNullOrWhiteSpace");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5c: Full query integration — type in WHERE
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealTypeFunctions_TypeOf_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var typeOfCall = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeOf", [typeof(object)])!,
            ObjectArg(nameProp));
        var condition = Expression.Equal(typeOfCall, Expression.Constant("string"));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("type::of");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsString_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var isStringCall = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsString", [typeof(object)])!,
            ObjectArg(nameProp));
        var condition = Expression.Equal(isStringCall, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("type::is_string");
    }

    [Test]
    public async Task SurrealTypeFunctions_IsInt_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var isIntCall = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("IsInt", [typeof(object)])!,
            ObjectArg(valueProp));
        var condition = Expression.Equal(isIntCall, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("type::is_int");
    }

    [Test]
    public async Task SurrealTypeFunctions_TypeInt_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var typeIntCall = Expression.Call(
            typeof(SurrealTypeFunctions).GetMethod("TypeInt", [typeof(object)])!,
            ObjectArg(valueProp));
        var condition = Expression.Equal(typeIntCall, Expression.Constant(null));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("type::int");
    }

}
