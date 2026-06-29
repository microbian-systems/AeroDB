using System.Linq.Expressions;
using TUnit.Core;

namespace Dali.Tests;

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
public class FunctionMappingTests
{
    // ════════════════════════════════════════════════════════════
    // Math interceptions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Math_Abs_TranslatesTo_MathAbs()
    {
        // x => Math.Abs(x.Value) > 5
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var absCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Abs), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(absCall, Expression.Constant(5.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::abs(Value) > 5");
    }

    [Test]
    public async Task Math_Ceiling_TranslatesTo_MathCeil()
    {
        // x => Math.Ceiling(x.Value) > 5
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var ceilCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Ceiling), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(ceilCall, Expression.Constant(5.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::ceil(Value) > 5");
    }

    [Test]
    public async Task Math_Floor_TranslatesTo_MathFloor()
    {
        // x => Math.Floor(x.Value) > 5
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var floorCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Floor), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(floorCall, Expression.Constant(5.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::floor(Value) > 5");
    }

    [Test]
    public async Task Math_Round_TranslatesTo_MathRound()
    {
        // x => Math.Round(x.Value) > 5
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var roundCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Round), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(roundCall, Expression.Constant(5.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::round(Value) > 5");
    }

    [Test]
    public async Task Math_Sqrt_TranslatesTo_MathSqrt()
    {
        // x => Math.Sqrt(x.Value) > 5
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var sqrtCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Sqrt), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(sqrtCall, Expression.Constant(5.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::sqrt(Value) > 5");
    }

    [Test]
    public async Task Math_Pow_TranslatesTo_MathPow()
    {
        // x => Math.Pow(x.Value, 2) > 5
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var powCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Pow), [typeof(double), typeof(double)])!,
            valueProp,
            Expression.Constant(2.0));
        var condition = Expression.GreaterThan(powCall, Expression.Constant(5.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::pow(Value, 2) > 5");
    }

    [Test]
    public async Task Math_Min_TranslatesTo_MathMin()
    {
        // x => Math.Min(x.Value, x.OtherValue) > 5
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var otherProp = Expression.Property(param, "OtherValue");
        var minCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Min), [typeof(double), typeof(double)])!,
            valueProp,
            otherProp);
        var condition = Expression.GreaterThan(minCall, Expression.Constant(5.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::min(Value, OtherValue) > 5");
    }

    [Test]
    public async Task Math_Max_TranslatesTo_MathMax()
    {
        // x => Math.Max(x.Value, x.OtherValue) > 5
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var otherProp = Expression.Property(param, "OtherValue");
        var maxCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Max), [typeof(double), typeof(double)])!,
            valueProp,
            otherProp);
        var condition = Expression.GreaterThan(maxCall, Expression.Constant(5.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::max(Value, OtherValue) > 5");
    }

    [Test]
    public async Task Math_Sin_TranslatesTo_MathSin()
    {
        // x => Math.Sin(x.Value) > 0
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var sinCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Sin), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(sinCall, Expression.Constant(0.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::sin(Value) > 0");
    }

    [Test]
    public async Task Math_Cos_TranslatesTo_MathCos()
    {
        // x => Math.Cos(x.Value) > 0
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var cosCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Cos), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(cosCall, Expression.Constant(0.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::cos(Value) > 0");
    }

    [Test]
    public async Task Math_Tan_TranslatesTo_MathTan()
    {
        // x => Math.Tan(x.Value) > 0
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var tanCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Tan), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(tanCall, Expression.Constant(0.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::tan(Value) > 0");
    }

    [Test]
    public async Task Math_Log_TranslatesTo_MathLn()
    {
        // x => Math.Log(x.Value) > 0  (natural log)
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var logCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Log), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(logCall, Expression.Constant(0.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::ln(Value) > 0");
    }

    [Test]
    public async Task Math_Log_WithBase_TranslatesTo_MathLog()
    {
        // x => Math.Log(x.Value, 10) > 0  (log with base)
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var logCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Log), [typeof(double), typeof(double)])!,
            valueProp,
            Expression.Constant(10.0));
        var condition = Expression.GreaterThan(logCall, Expression.Constant(0.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::log(Value, 10) > 0");
    }

    [Test]
    public async Task Math_Log10_TranslatesTo_MathLog10()
    {
        // x => Math.Log10(x.Value) > 0
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var log10Call = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Log10), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(log10Call, Expression.Constant(0.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::log10(Value) > 0");
    }

    [Test]
    public async Task Math_Sign_TranslatesTo_MathSign()
    {
        // x => Math.Sign(x.Value) > 0
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var signCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Sign), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(signCall, Expression.Constant(0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("math::sign(Value) > 0");
    }

    // ════════════════════════════════════════════════════════════
    // String interceptions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task String_Length_TranslatesTo_StringLength()
    {
        // x => x.Name.Length > 5
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var lengthProp = Expression.Property(nameProp, "Length");
        var condition = Expression.GreaterThan(lengthProp, Expression.Constant(5));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::length(Name) > 5");
    }

    [Test]
    public async Task String_Trim_TranslatesTo_StringTrim()
    {
        // x => x.Name.Trim() == "hello"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var trimCall = Expression.Call(nameProp, typeof(string).GetMethod("Trim", Type.EmptyTypes)!);
        var condition = Expression.Equal(trimCall, Expression.Constant("hello"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::trim(Name) = 'hello'");
    }

    [Test]
    public async Task String_ToUpper_TranslatesTo_StringUppercase()
    {
        // x => x.Name.ToUpper() == "HELLO"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var toUpperCall = Expression.Call(nameProp, typeof(string).GetMethod("ToUpper", Type.EmptyTypes)!);
        var condition = Expression.Equal(toUpperCall, Expression.Constant("HELLO"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::uppercase(Name) = 'HELLO'");
    }

    [Test]
    public async Task String_ToUpperInvariant_TranslatesTo_StringUppercase()
    {
        // x => x.Name.ToUpperInvariant() == "HELLO"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var toUpperCall = Expression.Call(nameProp, typeof(string).GetMethod("ToUpperInvariant", Type.EmptyTypes)!);
        var condition = Expression.Equal(toUpperCall, Expression.Constant("HELLO"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::uppercase(Name) = 'HELLO'");
    }

    [Test]
    public async Task String_ToLower_TranslatesTo_StringLowercase()
    {
        // x => x.Name.ToLower() == "hello"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var toLowerCall = Expression.Call(nameProp, typeof(string).GetMethod("ToLower", Type.EmptyTypes)!);
        var condition = Expression.Equal(toLowerCall, Expression.Constant("hello"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::lowercase(Name) = 'hello'");
    }

    [Test]
    public async Task String_ToLowerInvariant_TranslatesTo_StringLowercase()
    {
        // x => x.Name.ToLowerInvariant() == "hello"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var toLowerCall = Expression.Call(nameProp, typeof(string).GetMethod("ToLowerInvariant", Type.EmptyTypes)!);
        var condition = Expression.Equal(toLowerCall, Expression.Constant("hello"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::lowercase(Name) = 'hello'");
    }

    [Test]
    public async Task String_TrimStart_TranslatesTo_StringTrim()
    {
        // x => x.Name.TrimStart() == "hello"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var trimStartCall = Expression.Call(nameProp, typeof(string).GetMethod("TrimStart", Type.EmptyTypes)!);
        var condition = Expression.Equal(trimStartCall, Expression.Constant("hello"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::trim(Name) = 'hello'");
    }

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
    // Full query integration — Math in WHERE
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task MathAbs_InWhere_ProducesCorrectSurrealQL()
    {
        // Build: source.Where(x => Math.Abs(x.Value) > 5)
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var valueProp = Expression.Property(param, "Value");
        var absCall = Expression.Call(
            typeof(Math).GetMethod(nameof(Math.Abs), [typeof(double)])!,
            valueProp);
        var condition = Expression.GreaterThan(absCall, Expression.Constant(5.0));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("math::abs");
    }

    [Test]
    public async Task StringLength_InWhere_ProducesCorrectSurrealQL()
    {
        // Build: source.Where(x => x.Name.Length > 5)
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var lengthProp = Expression.Property(nameProp, "Length");
        var condition = Expression.GreaterThan(lengthProp, Expression.Constant(5));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::length");
    }

    [Test]
    public async Task StringTrim_InWhere_ProducesCorrectSurrealQL()
    {
        // Build: source.Where(x => x.Name.Trim() == "hello")
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var trimCall = Expression.Call(nameProp, typeof(string).GetMethod("Trim", Type.EmptyTypes)!);
        var condition = Expression.Equal(trimCall, Expression.Constant("hello"));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("string::trim");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5b: Extended string interceptions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task String_Replace_TranslatesTo_StringReplace()
    {
        // x => x.Name.Replace("a", "b") == "hello"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var replaceCall = Expression.Call(nameProp,
            typeof(string).GetMethod("Replace", [typeof(string), typeof(string)])!,
            Expression.Constant("a"), Expression.Constant("b"));
        var condition = Expression.Equal(replaceCall, Expression.Constant("hello"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::replace(Name, 'a', 'b') = 'hello'");
    }

    [Test]
    public async Task String_Substring_OneArg_TranslatesTo_StringSlice()
    {
        // x => x.Name.Substring(1) == "ello"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var substrCall = Expression.Call(nameProp,
            typeof(string).GetMethod("Substring", [typeof(int)])!,
            Expression.Constant(1));
        var condition = Expression.Equal(substrCall, Expression.Constant("ello"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::slice(Name, 1) = 'ello'");
    }

    [Test]
    public async Task String_Substring_TwoArgs_TranslatesTo_StringSlice()
    {
        // x => x.Name.Substring(1, 2) == "el"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var substrCall = Expression.Call(nameProp,
            typeof(string).GetMethod("Substring", [typeof(int), typeof(int)])!,
            Expression.Constant(1), Expression.Constant(2));
        var condition = Expression.Equal(substrCall, Expression.Constant("el"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::slice(Name, 1, 2) = 'el'");
    }

    [Test]
    public async Task String_Concat_TranslatesTo_StringConcat()
    {
        // x => string.Concat(x.Name, "suffix") == "hello"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var concatCall = Expression.Call(
            typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!,
            nameProp, Expression.Constant("suffix"));
        var condition = Expression.Equal(concatCall, Expression.Constant("hello"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::concat(Name, 'suffix') = 'hello'");
    }

    [Test]
    public async Task String_IndexOf_ThrowsNotSupportedException()
    {
        // x => x.Name.IndexOf('x') > 0
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var indexOfCall = Expression.Call(nameProp,
            typeof(string).GetMethod("IndexOf", [typeof(char)])!,
            Expression.Constant('x'));
        var condition = Expression.GreaterThan(indexOfCall, Expression.Constant(0));

        var ex = Should.Throw<NotSupportedException>(() =>
            SurrealExpressionVisitor.TranslateCondition(condition));

        ex.Message.ShouldContain("IndexOf");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5b: SurrealStringFunctions marker class
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealStringFunctions_Repeat_TranslatesCorrectly()
    {
        // SurrealStringFunctions.Repeat(x.Name, 3) == "aaa"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var repeatCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("Repeat", [typeof(string), typeof(int)])!,
            nameProp, Expression.Constant(3));
        var condition = Expression.Equal(repeatCall, Expression.Constant("aaa"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::repeat(Name, 3) = 'aaa'");
    }

    [Test]
    public async Task SurrealStringFunctions_Reverse_TranslatesCorrectly()
    {
        // SurrealStringFunctions.Reverse(x.Name) == "olleh"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var reverseCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("Reverse", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(reverseCall, Expression.Constant("olleh"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::reverse(Name) = 'olleh'");
    }

    [Test]
    public async Task SurrealStringFunctions_SimilarityFuzzy_TranslatesCorrectly()
    {
        // SurrealStringFunctions.SimilarityFuzzy(x.Name, y.Name) > 0
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var otherParam = Expression.Parameter(typeof(FunctionTestDoc), "y");
        var nameProp = Expression.Property(param, "Name");
        var otherNameProp = Expression.Property(otherParam, "Name");
        var simCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("SimilarityFuzzy",
                [typeof(string), typeof(string)])!,
            nameProp, otherNameProp);
        var condition = Expression.GreaterThan(simCall, Expression.Constant(0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::similarity::fuzzy(Name, Name) > 0");
    }

    [Test]
    public async Task SurrealStringFunctions_SimilarityJaro_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var otherParam = Expression.Parameter(typeof(FunctionTestDoc), "y");
        var nameProp = Expression.Property(param, "Name");
        var otherNameProp = Expression.Property(otherParam, "Name");
        var simCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("SimilarityJaro",
                [typeof(string), typeof(string)])!,
            nameProp, otherNameProp);
        var condition = Expression.GreaterThan(simCall, Expression.Constant(0.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::similarity::jaro(Name, Name) > 0");
    }

    [Test]
    public async Task SurrealStringFunctions_SimilarityJaroWinkler_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var otherParam = Expression.Parameter(typeof(FunctionTestDoc), "y");
        var nameProp = Expression.Property(param, "Name");
        var otherNameProp = Expression.Property(otherParam, "Name");
        var simCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("SimilarityJaroWinkler",
                [typeof(string), typeof(string)])!,
            nameProp, otherNameProp);
        var condition = Expression.GreaterThan(simCall, Expression.Constant(0.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::similarity::jaro_winkler(Name, Name) > 0");
    }

    [Test]
    public async Task SurrealStringFunctions_DistanceLevenshtein_TranslatesCorrectly()
    {
        // SurrealStringFunctions.DistanceLevenshtein(x.Name, y.Name) > 0
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var otherParam = Expression.Parameter(typeof(FunctionTestDoc), "y");
        var nameProp = Expression.Property(param, "Name");
        var otherNameProp = Expression.Property(otherParam, "Name");
        var distCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("DistanceLevenshtein",
                [typeof(string), typeof(string)])!,
            nameProp, otherNameProp);
        var condition = Expression.GreaterThan(distCall, Expression.Constant(0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::distance::levenshtein(Name, Name) > 0");
    }

    [Test]
    public async Task SurrealStringFunctions_DistanceHamming_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var otherParam = Expression.Parameter(typeof(FunctionTestDoc), "y");
        var nameProp = Expression.Property(param, "Name");
        var otherNameProp = Expression.Property(otherParam, "Name");
        var distCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("DistanceHamming",
                [typeof(string), typeof(string)])!,
            nameProp, otherNameProp);
        var condition = Expression.GreaterThan(distCall, Expression.Constant(0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::distance::hamming(Name, Name) > 0");
    }

    [Test]
    public async Task SurrealStringFunctions_DistanceDamerauLevenshtein_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var otherParam = Expression.Parameter(typeof(FunctionTestDoc), "y");
        var nameProp = Expression.Property(param, "Name");
        var otherNameProp = Expression.Property(otherParam, "Name");
        var distCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("DistanceDamerauLevenshtein",
                [typeof(string), typeof(string)])!,
            nameProp, otherNameProp);
        var condition = Expression.GreaterThan(distCall, Expression.Constant(0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::distance::damerau_levenshtein(Name, Name) > 0");
    }

    [Test]
    public async Task SurrealStringFunctions_DistanceNormalizedLevenshtein_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var otherParam = Expression.Parameter(typeof(FunctionTestDoc), "y");
        var nameProp = Expression.Property(param, "Name");
        var otherNameProp = Expression.Property(otherParam, "Name");
        var distCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("DistanceNormalizedLevenshtein",
                [typeof(string), typeof(string)])!,
            nameProp, otherNameProp);
        var condition = Expression.GreaterThan(distCall, Expression.Constant(0.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::distance::normalized_levenshtein(Name, Name) > 0");
    }

    [Test]
    public async Task SurrealStringFunctions_DistanceNormalizedDamerauLevenshtein_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var otherParam = Expression.Parameter(typeof(FunctionTestDoc), "y");
        var nameProp = Expression.Property(param, "Name");
        var otherNameProp = Expression.Property(otherParam, "Name");
        var distCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("DistanceNormalizedDamerauLevenshtein",
                [typeof(string), typeof(string)])!,
            nameProp, otherNameProp);
        var condition = Expression.GreaterThan(distCall, Expression.Constant(0.0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::distance::normalized_damerau_levenshtein(Name, Name) > 0");
    }

    [Test]
    public async Task SurrealStringFunctions_DistanceOsa_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var otherParam = Expression.Parameter(typeof(FunctionTestDoc), "y");
        var nameProp = Expression.Property(param, "Name");
        var otherNameProp = Expression.Property(otherParam, "Name");
        var distCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("DistanceOsa",
                [typeof(string), typeof(string)])!,
            nameProp, otherNameProp);
        var condition = Expression.GreaterThan(distCall, Expression.Constant(0));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::distance::osa(Name, Name) > 0");
    }

    [Test]
    public async Task SurrealStringFunctions_IsAlphanum_TranslatesCorrectly()
    {
        // SurrealStringFunctions.IsAlphanum(x.Name) == true
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsAlphanum", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::is::alphanum(Name) = true");
    }

    [Test]
    public async Task SurrealStringFunctions_IsAlpha_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsAlpha", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::is::alpha(Name) = true");
    }

    [Test]
    public async Task SurrealStringFunctions_IsAscii_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsAscii", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::is::ascii(Name) = true");
    }

    [Test]
    public async Task SurrealStringFunctions_IsEmail_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsEmail", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::is::email(Name) = true");
    }

    [Test]
    public async Task SurrealStringFunctions_IsUrl_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsUrl", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::is::url(Name) = true");
    }

    [Test]
    public async Task SurrealStringFunctions_IsUuid_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsUuid", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::is::uuid(Name) = true");
    }

    [Test]
    public async Task SurrealStringFunctions_IsNumeric_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsNumeric", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::is::numeric(Name) = true");
    }

    [Test]
    public async Task SurrealStringFunctions_Join_TranslatesCorrectly()
    {
        // SurrealStringFunctions.Join(",", x.Name, "suffix") == "value,suffix"
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var joinCall = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("Join", [typeof(string), typeof(string[])])!,
            Expression.Constant(","),
            Expression.NewArrayInit(typeof(string), nameProp, Expression.Constant("suffix")));
        var condition = Expression.Equal(joinCall, Expression.Constant("value,suffix"));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::join(',', Name, 'suffix') = 'value,suffix'");
    }

    [Test]
    public async Task SurrealStringFunctions_IsDatetime_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealStringFunctions).GetMethod("IsDatetime", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant(true));

        var result = SurrealExpressionVisitor.TranslateCondition(condition);
        result.ShouldBe("string::is::datetime(Name) = true");
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

    // ════════════════════════════════════════════════════════════
    // Phase 5d: SurrealCryptoFunctions — expression-level tests
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealCryptoFunctions_Argon2Generate_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Argon2Generate", [typeof(string)])!,
            nameProp);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("h")));
        result.ShouldBe("crypto::argon2::generate(Name) = 'h'");
    }

    [Test]
    public async Task SurrealCryptoFunctions_Argon2Compare_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Argon2Compare", [typeof(string), typeof(string)])!,
            nameProp, Expression.Constant("hash"));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("crypto::argon2::compare(Name, 'hash') = true");
    }

    [Test]
    public async Task SurrealCryptoFunctions_BcryptGenerate_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("BcryptGenerate", [typeof(string)])!,
            nameProp);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("h")));
        result.ShouldBe("crypto::bcrypt::generate(Name) = 'h'");
    }

    [Test]
    public async Task SurrealCryptoFunctions_BcryptCompare_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("BcryptCompare", [typeof(string), typeof(string)])!,
            nameProp, Expression.Constant("hash"));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("crypto::bcrypt::compare(Name, 'hash') = true");
    }

    [Test]
    public async Task SurrealCryptoFunctions_Pbkdf2Generate_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Pbkdf2Generate", [typeof(string)])!,
            nameProp);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("h")));
        result.ShouldBe("crypto::pbkdf2::generate(Name) = 'h'");
    }

    [Test]
    public async Task SurrealCryptoFunctions_Pbkdf2Compare_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Pbkdf2Compare", [typeof(string), typeof(string)])!,
            nameProp, Expression.Constant("hash"));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("crypto::pbkdf2::compare(Name, 'hash') = true");
    }

    [Test]
    public async Task SurrealCryptoFunctions_ScryptGenerate_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("ScryptGenerate", [typeof(string)])!,
            nameProp);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("h")));
        result.ShouldBe("crypto::scrypt::generate(Name) = 'h'");
    }

    [Test]
    public async Task SurrealCryptoFunctions_ScryptCompare_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("ScryptCompare", [typeof(string), typeof(string)])!,
            nameProp, Expression.Constant("hash"));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldBe("crypto::scrypt::compare(Name, 'hash') = true");
    }

    [Test]
    public async Task SurrealCryptoFunctions_Md5_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Md5", [typeof(string)])!,
            nameProp);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("d5")));
        result.ShouldBe("crypto::md5(Name) = 'd5'");
    }

    [Test]
    public async Task SurrealCryptoFunctions_Sha1_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Sha1", [typeof(string)])!,
            nameProp);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("a")));
        result.ShouldBe("crypto::sha1(Name) = 'a'");
    }

    [Test]
    public async Task SurrealCryptoFunctions_Sha256_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Sha256", [typeof(string)])!,
            nameProp);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("b")));
        result.ShouldBe("crypto::sha256(Name) = 'b'");
    }

    [Test]
    public async Task SurrealCryptoFunctions_Sha512_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Sha512", [typeof(string)])!,
            nameProp);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("c")));
        result.ShouldBe("crypto::sha512(Name) = 'c'");
    }

    [Test]
    public async Task SurrealRandFunctions_UuidV4_TranslatesCorrectly()
    {
        var call = Expression.Call(
            typeof(SurrealRandFunctions).GetMethod("UuidV4", Type.EmptyTypes)!);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("u")));
        result.ShouldBe("rand::uuid::v4() = 'u'");
    }

    [Test]
    public async Task SurrealRandFunctions_UuidV7_TranslatesCorrectly()
    {
        var call = Expression.Call(
            typeof(SurrealRandFunctions).GetMethod("UuidV7", Type.EmptyTypes)!);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("u")));
        result.ShouldBe("rand::uuid::v7() = 'u'");
    }

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
        result.ShouldBe("search::highlight('<b>', '</b>', Name) = 'r'");
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
        result.ShouldBe("search::offsets(Name) = 'r'");
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
        result.ShouldBe("search::analyze(Name, 'english') = 'r'");
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

    // ════════════════════════════════════════════════════════════
    // Phase 5d: Crypto integration — InWhere
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Argon2Compare_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Argon2Compare", [typeof(string), typeof(string)])!,
            nameProp, Expression.Constant("hash"));
        var condition = Expression.Equal(call, Expression.Constant(true));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("crypto::argon2::compare");
    }

    [Test]
    public async Task Md5_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Md5", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant("d5"));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("crypto::md5");
    }

    [Test]
    public async Task Sha256_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var call = Expression.Call(
            typeof(SurrealCryptoFunctions).GetMethod("Sha256", [typeof(string)])!,
            nameProp);
        var condition = Expression.Equal(call, Expression.Constant("b"));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("crypto::sha256");
    }

    [Test]
    public async Task UuidV4_InWhere_ProducesCorrectSurrealQL()
    {
        var visitor = new SurrealExpressionVisitor();
        var source = new List<FunctionTestDoc>().AsQueryable();

        var whereMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(FunctionTestDoc));

        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var call = Expression.Call(
            typeof(SurrealRandFunctions).GetMethod("UuidV4", Type.EmptyTypes)!);
        var condition = Expression.Equal(call, Expression.Constant("u"));
        var lambda = Expression.Lambda<Func<FunctionTestDoc, bool>>(condition, param);

        var expression = Expression.Call(whereMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Where.Count.ShouldBe(1);
        result.Where[0].ShouldContain("rand::uuid::v4");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5d: Unsupported crypto method
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealCryptoFunctions_UnknownMethod_ThrowsNotSupportedException()
    {
        // string.IsNullOrWhiteSpace is a static string method not handled by our switch.
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var ex = Should.Throw<NotSupportedException>(() =>
            SurrealExpressionVisitor.TranslateCondition(
                Expression.Call(
                    typeof(string).GetMethod("IsNullOrWhiteSpace", [typeof(string)])!,
                    nameProp)));
        ex.Message.ShouldContain("IsNullOrWhiteSpace");
    }

    // ════════════════════════════════════════════════════════════
    // Phase 5e: SurrealArrayFunctions — expression-level tests
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealArrayFunctions_Add_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var valConst = Expression.Constant("c");
        var addMethod = typeof(SurrealArrayFunctions).GetMethod("Add").MakeGenericMethod(typeof(string));
        var call = Expression.Call(addMethod, arrConst, valConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::add(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Append_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var valConst = Expression.Constant("c");
        var method = typeof(SurrealArrayFunctions).GetMethod("Append").MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst, valConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::append(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Prepend_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var valConst = Expression.Constant("c");
        var method = typeof(SurrealArrayFunctions).GetMethod("Prepend").MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst, valConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::prepend(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Remove_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var method = typeof(SurrealArrayFunctions).GetMethod("Remove").MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst, Expression.Constant(0));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::remove(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Sort_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "b", "a" });
        var method = typeof(SurrealArrayFunctions).GetMethod("Sort").MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::sort(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Distinct_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b", "a" });
        var method = typeof(SurrealArrayFunctions).GetMethod("Distinct").MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::distinct(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Union_TranslatesCorrectly()
    {
        var arr1 = Expression.Constant(new string[] { "a", "b" });
        var arr2 = Expression.Constant(new string[] { "c", "d" });
        var method = typeof(SurrealArrayFunctions).GetMethod("Union").MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arr1, arr2);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldContain("array::union(");
    }

    [Test]
    public async Task SurrealArrayFunctions_ArrayContains_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var valConst = Expression.Constant("a");
        var method = typeof(SurrealArrayFunctions).GetMethod("ArrayContains").MakeGenericMethod(typeof(string));
        var call = Expression.Call(method, arrConst, valConst);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(true)));
        result.ShouldContain("array::contains(");
    }

    [Test]
    public async Task SurrealArrayFunctions_Len_TranslatesCorrectly()
    {
        var arrConst = Expression.Constant(new string[] { "a", "b" });
        var method = typeof(SurrealArrayFunctions).GetMethod("Len").MakeGenericMethod(typeof(string));
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
        var method = typeof(SurrealArrayFunctions).GetMethod("ArrayContains").MakeGenericMethod(typeof(string));
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
        var method = typeof(SurrealArrayFunctions).GetMethod("Len").MakeGenericMethod(typeof(string));
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

    // ════════════════════════════════════════════════════════════
    // Phase 5f: Expanded rand functions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task SurrealRandFunctions_Ulid_TranslatesCorrectly()
    {
        var call = Expression.Call(typeof(SurrealRandFunctions).GetMethod("Ulid", Type.EmptyTypes)!);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("u")));
        result.ShouldBe("rand::ulid() = 'u'");
    }

    [Test]
    public async Task SurrealRandFunctions_Int_TranslatesCorrectly()
    {
        var method = typeof(SurrealRandFunctions).GetMethod("Int", [typeof(int), typeof(int)])!;
        var call = Expression.Call(method, Expression.Constant(1), Expression.Constant(10));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0)));
        result.ShouldContain("rand::int(1, 10)");
    }

    [Test]
    public async Task SurrealRandFunctions_Float_TranslatesCorrectly()
    {
        var method = typeof(SurrealRandFunctions).GetMethod("Float", [typeof(double), typeof(double)])!;
        var call = Expression.Call(method, Expression.Constant(0.0), Expression.Constant(1.0));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0.0)));
        result.ShouldContain("rand::float(0, 1)");
    }

    [Test]
    public async Task SurrealRandFunctions_Guid_TranslatesCorrectly()
    {
        var call = Expression.Call(typeof(SurrealRandFunctions).GetMethod("Guid", Type.EmptyTypes)!);
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant("g")));
        result.ShouldBe("rand::guid() = 'g'");
    }

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
        result.ShouldBe("object::keys(Name) = NONE");
    }

    [Test]
    public async Task SurrealObjectFunctions_Values_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var method = typeof(SurrealObjectFunctions).GetMethod("Values", [typeof(object)])!;
        var call = Expression.Call(method, Expression.Convert(nameProp, typeof(object)));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.Equal(call, Expression.Constant(null)));
        result.ShouldBe("object::values(Name) = NONE");
    }

    [Test]
    public async Task SurrealObjectFunctions_Len_TranslatesCorrectly()
    {
        var param = Expression.Parameter(typeof(FunctionTestDoc), "x");
        var nameProp = Expression.Property(param, "Name");
        var method = typeof(SurrealObjectFunctions).GetMethod("Len", [typeof(object)])!;
        var call = Expression.Call(method, Expression.Convert(nameProp, typeof(object)));
        var result = SurrealExpressionVisitor.TranslateCondition(Expression.GreaterThan(call, Expression.Constant(0)));
        result.ShouldBe("object::len(Name) > 0");
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
        result.Projection.ShouldBe("math::sum(math::abs(Value))");
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
        result.Projection.ShouldBe("math::min(math::round(Value))");
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
        result.Projection.ShouldBe("math::max(math::sqrt(Value))");
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
        result.Projection.ShouldBe("math::mean(math::abs(Value))");
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
        result.Projection.ShouldContain("math::abs(Value) AS Value");
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
        result.Projection.ShouldContain("string::trim(Name) AS Name");
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
        result.Projection.ShouldContain("type::of(Name) AS Name");
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
        var lenMethod = typeof(SurrealArrayFunctions).GetMethod("Len").MakeGenericMethod(typeof(string));
        var lenCall = Expression.Call(lenMethod, tagsProp);

        var valueProp = typeof(FunctionTestDoc).GetProperty("Value")!;
        var bind = Expression.Bind(valueProp, Expression.Convert(lenCall, typeof(double)));
        var init = Expression.MemberInit(Expression.New(typeof(FunctionTestDoc)), bind);

        var lambda = Expression.Lambda<Func<FunctionTestDoc, FunctionTestDoc>>(init, param);

        var expression = Expression.Call(selectMethod, source.Expression,
            Expression.Quote(lambda));

        var result = visitor.Translate(expression);
        result.Projection.ShouldContain("array::len(Tags) AS Value");
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
        result.OrderBy[0].ShouldBe("math::abs(Value) ASC");
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
        result.OrderBy[0].ShouldBe("string::length(Name) DESC");
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
        result.GroupBy[0].ShouldBe("type::of(Name)");
    }
}
