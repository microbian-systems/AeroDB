using System.Linq.Expressions;
using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

public partial class FunctionMappingTests
{
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

}
