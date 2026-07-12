using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using AeroDB.Analyzers;

namespace AeroDB.Analyzers.Tests;

public class ComputedPropertyAnalyzerTests
{
    private static Task VerifyAnalyzerAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<ComputedPropertyAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80
        };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync(CancellationToken.None);
    }

    private static DiagnosticResult Diagnostic(string diagnosticId)
        => CSharpAnalyzerVerifier<ComputedPropertyAnalyzer, DefaultVerifier>.Diagnostic(diagnosticId);

    // Helper: wraps source with stubs for SableDocument and ISableDocument.
    // System.Text.Json is already available via ReferenceAssemblies.Net.Net80,
    // so no JsonIgnoreAttribute stub is needed.
    private static string WrapWithStubs(string body) => @"
namespace AeroDB.Sable
{
    public class SableDocument { public long Id { get; set; } }
    public interface ISableDocument<TId> { }
}
" + body;

    // ── Test 1: Computed property without [JsonIgnore] → ADB001 ──
    [Test]
    public async Task ComputedProperty_WithoutJsonIgnore_EmitsADB001()
    {
        var source = WrapWithStubs(@"
public class MyDoc : AeroDB.Sable.SableDocument
{
    public bool IsActive => true;
}");
        // Stubs occupy lines 1-7 (7 lines including the leading empty line).
        // The property "IsActive" is on full-source line 10, column 17.
        var expected = Diagnostic("ADB001").WithArguments("IsActive").WithLocation(10, 17);
        await VerifyAnalyzerAsync(source, expected);
    }

    // ── Test 2: Computed property with [JsonIgnore] → no diagnostic ──
    [Test]
    public async Task ComputedProperty_WithJsonIgnore_NoDiagnostic()
    {
        var source = WrapWithStubs(@"
public class MyDoc : AeroDB.Sable.SableDocument
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => true;
}");
        await VerifyAnalyzerAsync(source);
    }

    // ── Test 3: Read-write property → no diagnostic ──
    [Test]
    public async Task ReadWriteProperty_NoDiagnostic()
    {
        var source = WrapWithStubs(@"
public class MyDoc : AeroDB.Sable.SableDocument
{
    public bool IsActive { get; set; }
}");
        await VerifyAnalyzerAsync(source);
    }

    // ── Test 4: Non-SableDocument subclass → no diagnostic ──
    [Test]
    public async Task NonSableDocument_NoDiagnostic()
    {
        var source = WrapWithStubs(@"
public class PlainClass
{
    public bool IsActive => true;
}");
        await VerifyAnalyzerAsync(source);
    }

    // ── Test 5: Static computed property → no diagnostic ──
    [Test]
    public async Task StaticComputedProperty_NoDiagnostic()
    {
        var source = WrapWithStubs(@"
public class MyDoc : AeroDB.Sable.SableDocument
{
    public static bool IsActive => true;
}");
        await VerifyAnalyzerAsync(source);
    }

    // ── Test 6: ISableDocument<TId> interface implementation → ADB001 ──
    [Test]
    public async Task ISableDocumentInterface_ComputedProperty_EmitsADB001()
    {
        var source = WrapWithStubs(@"
public class MyDoc : AeroDB.Sable.ISableDocument<long>
{
    public long Id { get; set; }
    public bool IsActive => true;
}");
        // "IsActive" is on full-source line 11, column 17.
        var expected = Diagnostic("ADB001").WithArguments("IsActive").WithLocation(11, 17);
        await VerifyAnalyzerAsync(source, expected);
    }

    // ── Test 7: Multiple computed properties → multiple ADB001 ──
    [Test]
    public async Task MultipleComputedProperties_MultipleADB001()
    {
        var source = WrapWithStubs(@"
public class MyDoc : AeroDB.Sable.SableDocument
{
    public bool IsActiveA => true;
    public string StatusName => ""active"";
}");
        // "IsActiveA" is on line 10, col 17; "StatusName" is on line 11, col 19.
        await VerifyAnalyzerAsync(source,
            Diagnostic("ADB001").WithArguments("IsActiveA").WithLocation(10, 17),
            Diagnostic("ADB001").WithArguments("StatusName").WithLocation(11, 19));
    }

    // ── Test 8: Expression-bodied with body block (not =>) → ADB001 ──
    [Test]
    public async Task ExpressionBodiedBlockStyle_EmitsADB001()
    {
        var source = WrapWithStubs(@"
public class MyDoc : AeroDB.Sable.SableDocument
{
    public bool IsActive
    {
        get { return true; }
    }
}");
        // "IsActive" is on line 10, column 17.
        var expected = Diagnostic("ADB001").WithArguments("IsActive").WithLocation(10, 17);
        await VerifyAnalyzerAsync(source, expected);
    }
}
