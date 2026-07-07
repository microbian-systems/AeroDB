using System.Linq.Expressions;
using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

public partial class FunctionMappingTests
{
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
        result.ShouldBe("string::length(name) > 5");
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
        result.ShouldBe("string::trim(name) = 'hello'");
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
        result.ShouldBe("string::uppercase(name) = 'HELLO'");
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
        result.ShouldBe("string::uppercase(name) = 'HELLO'");
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
        result.ShouldBe("string::lowercase(name) = 'hello'");
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
        result.ShouldBe("string::lowercase(name) = 'hello'");
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
        result.ShouldBe("string::trim(name) = 'hello'");
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
        result.ShouldBe("string::replace(name, 'a', 'b') = 'hello'");
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
        result.ShouldBe("string::slice(name, 1) = 'ello'");
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
        result.ShouldBe("string::slice(name, 1, 2) = 'el'");
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
        result.ShouldBe("string::concat(name, 'suffix') = 'hello'");
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
        result.ShouldBe("string::repeat(name, 3) = 'aaa'");
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
        result.ShouldBe("string::reverse(name) = 'olleh'");
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
        result.ShouldBe("string::similarity::fuzzy(name, name) > 0");
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
        result.ShouldBe("string::similarity::jaro(name, name) > 0");
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
        result.ShouldBe("string::similarity::jaro_winkler(name, name) > 0");
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
        result.ShouldBe("string::distance::levenshtein(name, name) > 0");
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
        result.ShouldBe("string::distance::hamming(name, name) > 0");
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
        result.ShouldBe("string::distance::damerau_levenshtein(name, name) > 0");
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
        result.ShouldBe("string::distance::normalized_levenshtein(name, name) > 0");
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
        result.ShouldBe("string::distance::normalized_damerau_levenshtein(name, name) > 0");
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
        result.ShouldBe("string::distance::osa(name, name) > 0");
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
        result.ShouldBe("string::is::alphanum(name) = true");
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
        result.ShouldBe("string::is::alpha(name) = true");
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
        result.ShouldBe("string::is::ascii(name) = true");
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
        result.ShouldBe("string::is::email(name) = true");
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
        result.ShouldBe("string::is::url(name) = true");
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
        result.ShouldBe("string::is::uuid(name) = true");
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
        result.ShouldBe("string::is::numeric(name) = true");
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
        result.ShouldBe("string::join(',', name, 'suffix') = 'value,suffix'");
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
        result.ShouldBe("string::is::datetime(name) = true");
    }

}
