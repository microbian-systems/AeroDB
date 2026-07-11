using System.Linq.Expressions;
using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

public partial class FunctionMappingTests
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
        result.ShouldBe("math::abs(value) > 5");
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
        result.ShouldBe("math::ceil(value) > 5");
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
        result.ShouldBe("math::floor(value) > 5");
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
        result.ShouldBe("math::round(value) > 5");
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
        result.ShouldBe("math::sqrt(value) > 5");
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
        result.ShouldBe("math::pow(value, 2) > 5");
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
        result.ShouldBe("math::min(value, other_value) > 5");
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
        result.ShouldBe("math::max(value, other_value) > 5");
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
        result.ShouldBe("math::sin(value) > 0");
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
        result.ShouldBe("math::cos(value) > 0");
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
        result.ShouldBe("math::tan(value) > 0");
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
        result.ShouldBe("math::ln(value) > 0");
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
        result.ShouldBe("math::log(value, 10) > 0");
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
        result.ShouldBe("math::log10(value) > 0");
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
        result.ShouldBe("math::sign(value) > 0");
    }

}
