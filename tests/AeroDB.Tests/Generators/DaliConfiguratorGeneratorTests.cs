using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using AeroDB.SourceGenerators;
using TUnit.Core;

namespace AeroDB.Tests.Generators;

/// <summary>
/// Additional edge-case tests for <see cref="DaliConfiguratorGenerator"/>.
/// See <c>AeroDB.Tests.DaliConfiguratorGeneratorTests</c> for the core scenario tests.
/// </summary>
public class DaliConfiguratorGeneratorEdgeCaseTests
{
    /// <summary>
    /// Runs the <see cref="DaliConfiguratorGenerator"/> in-process.
    /// </summary>
    private static GeneratorDriverRunResult RunGenerator(params string[] sources)
    {
        var syntaxTrees = sources
            .Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest)))
            .ToArray();

        _ = typeof(IServiceCollection);

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name != "AeroDB" && name != "AeroDB.SourceGenerators";
            })
            .GroupBy(a => a.Location)
            .Select(g => MetadataReference.CreateFromFile(g.Key))
            .Cast<MetadataReference>()
            .ToArray();

        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        var generator = new DaliConfiguratorGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        return driver.RunGenerators(compilation).GetRunResult();
    }

    // ── Test 1: Static classes are filtered out ──────────────────────────────

    [Test]
    public void Static_class_is_skipped()
    {
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureDali { void Configure(StoreOptions options); }
    public class StoreOptions { }
}

public static class StaticConfigurator : AeroDB.IConfigureDali
{
    public static void Configure(AeroDB.StoreOptions options) { }
}

public class InstanceConfigurator : AeroDB.IConfigureDali
{
    public void Configure(AeroDB.StoreOptions options) { }
}
";
        var result = RunGenerator(source);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("InstanceConfigurator");
        code.ShouldNotContain("StaticConfigurator");
    }

    // ── Test 2: Generic types are filtered out ──────────────────────────────

    [Test]
    public void Generic_type_is_skipped()
    {
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureDali { void Configure(StoreOptions options); }
    public class StoreOptions { }
}

public class GenericConfigurator<T> : AeroDB.IConfigureDali
{
    public void Configure(AeroDB.StoreOptions options) { }
}

public class SimpleConfigurator : AeroDB.IConfigureDali
{
    public void Configure(AeroDB.StoreOptions options) { }
}
";
        var result = RunGenerator(source);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("SimpleConfigurator");
        code.ShouldNotContain("GenericConfigurator");
    }

    // ── Test 3: Non-public types are filtered out ────────────────────────────

    [Test]
    public void Non_public_type_is_skipped()
    {
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureDali { void Configure(StoreOptions options); }
    public class StoreOptions { }
}

class InternalConfigurator : AeroDB.IConfigureDali
{
    public void Configure(AeroDB.StoreOptions options) { }
}
";
        var result = RunGenerator(source);
        var code = result.GeneratedTrees[0].ToString();

        // Internal classes ARE accessible (DeclaredAccessibility.Internal is allowed)
        code.ShouldContain("InternalConfigurator");
    }

    // ── Test 4: Private nested type is filtered out ──────────────────────────

    [Test]
    public void Private_nested_type_is_skipped()
    {
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureDali { void Configure(StoreOptions options); }
    public class StoreOptions { }
}

public class Outer
{
    private class PrivateConfigurator : AeroDB.IConfigureDali
    {
        public void Configure(AeroDB.StoreOptions options) { }
    }

    public class PublicConfigurator : AeroDB.IConfigureDali
    {
        public void Configure(AeroDB.StoreOptions options) { }
    }
}
";
        var result = RunGenerator(source);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("PublicConfigurator");
        code.ShouldNotContain("PrivateConfigurator");
    }

    // ── Test 5: IGlobalConfigureDali registered separately ───────────────────

    [Test]
    public void Global_configurator_registered_as_global()
    {
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureDali { void Configure(StoreOptions options); }
    public interface IGlobalConfigureDali : IConfigureDali { }
    public class StoreOptions { }
}

public class MyGlobalConfigurator : AeroDB.IGlobalConfigureDali
{
    public void Configure(AeroDB.StoreOptions options) { }
}
";
        var result = RunGenerator(source);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("MyGlobalConfigurator");
        code.ShouldContain("IGlobalConfigureDali");
        // Should NOT also register as IConfigureDali (exclusive)
        // Generated code uses FullyQualifiedFormat → global:: prefix
        var icdCount = CountOccurrences(code, "IConfigureDali, global::MyGlobalConfigurator");
        icdCount.ShouldBe(0);
    }

    // ── Test 6: IConfigureDali<TStore> generic variant ───────────────────────

    [Test]
    public void Typed_IConfigureDali_of_T_is_discovered()
    {
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureDali { void Configure(StoreOptions options); }
    public interface IConfigureDali<TStore> : IConfigureDali { }
    public class StoreOptions { }
}

public class TypedConfigurator : AeroDB.IConfigureDali<int>
{
    public void Configure(AeroDB.StoreOptions options) { }
}
";
        var result = RunGenerator(source);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("TypedConfigurator");
        code.ShouldContain("IConfigureDali");
    }

    // ── Test 7: Both sync and async on same class ────────────────────────────

    [Test]
    public void Class_implementing_both_sync_and_async_is_registered_once()
    {
        var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureDali { void Configure(StoreOptions options); }
    public interface IAsyncConfigureDali { Task ConfigureAsync(StoreOptions options, CancellationToken ct = default); }
    public class StoreOptions { }
}

public class DualConfigurator : AeroDB.IConfigureDali, AeroDB.IAsyncConfigureDali
{
    public void Configure(AeroDB.StoreOptions options) { }
    public Task ConfigureAsync(AeroDB.StoreOptions options, CancellationToken ct) => Task.CompletedTask;
}
";
        var result = RunGenerator(source);
        var code = result.GeneratedTrees[0].ToString();

        // Should be registered as both sync and async
        // Generated code uses FullyQualifiedFormat → global:: prefix
        code.ShouldContain("IConfigureDali, global::DualConfigurator");
        code.ShouldContain("IAsyncConfigureDali, global::DualConfigurator");

        // Should appear only once per registration type (not duplicated)
        var syncCount = CountOccurrences(code, "IConfigureDali, global::DualConfigurator");
        syncCount.ShouldBe(1);
    }

    // ── Test 8: No matching interfaces → no output ──────────────────────────

    [Test]
    public void No_matching_configurators_generates_empty_registrar()
    {
        // Must define IConfigureDali so the generator passes its early-return
        // guard and proceeds to emit an empty registrar.
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureDali { void Configure(StoreOptions options); }
    public class StoreOptions { }
}

public class PlainClass { }
public class AlsoPlain { public int X { get; set; } }
";
        var result = RunGenerator(source);

        // Generator should produce exactly one tree (the empty registrar)
        result.GeneratedTrees.Length.ShouldBe(1);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("AddDiscoveredDaliConfigurators");
        code.ShouldContain("return services;");
        code.ShouldNotContain("AddSingleton");
        // Non-configurator classes should not appear in generated code
        code.ShouldNotContain("PlainClass");
        code.ShouldNotContain("AlsoPlain");
    }

    // ── Test 9: Generated output compiles ───────────────────────────────────

    [Test]
    public void Generated_output_compiles()
    {
        var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureDali { void Configure(StoreOptions options); }
    public interface IGlobalConfigureDali : IConfigureDali { }
    public interface IAsyncConfigureDali { Task ConfigureAsync(StoreOptions options, CancellationToken ct = default); }
    public class StoreOptions { }
}

public class SyncCfg : AeroDB.IConfigureDali
{
    public void Configure(AeroDB.StoreOptions options) { }
}

public class GlobalCfg : AeroDB.IGlobalConfigureDali
{
    public void Configure(AeroDB.StoreOptions options) { }
}

public class AsyncCfg : AeroDB.IAsyncConfigureDali
{
    public Task ConfigureAsync(AeroDB.StoreOptions options, CancellationToken ct) => Task.CompletedTask;
}
";
        var result = RunGenerator(source);
        var generatedCode = result.GeneratedTrees[0].ToString();

        // Combine user source + generated code
        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)),
            CSharpSyntaxTree.ParseText(generatedCode, new CSharpParseOptions(LanguageVersion.Latest)),
        };

        _ = typeof(IServiceCollection);

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToArray();

        var compilation = CSharpCompilation.Create("Generated",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        errors.Count.ShouldBe(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
