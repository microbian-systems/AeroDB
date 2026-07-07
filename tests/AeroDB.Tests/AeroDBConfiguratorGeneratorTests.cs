using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using AeroDB.SourceGenerators;
using TUnit.Core;

namespace AeroDB.Tests;

public class AeroDBConfiguratorGeneratorTests
{
    /// <summary>
    /// Runs the <see cref="AeroDBConfiguratorGenerator"/> in-process over the given
    /// C# source snippets and returns the generator driver result.
    /// </summary>
    /// <param name="sources">One or more C# source code strings.</param>
    private static GeneratorDriverRunResult RunGenerator(params string[] sources)
    {
        var syntaxTrees = sources
            .Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest)))
            .ToArray();

        // Force-load the DI abstractions assembly so it appears in AppDomain.GetAssemblies()
        _ = typeof(IServiceCollection);

        // Collect all loaded assemblies as metadata references, excluding the real
        // AeroDB assembly so inline interface definitions don't conflict with the
        // project-level AeroDB.dll reference.
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
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new AeroDBConfiguratorGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        return driver.RunGenerators(compilation).GetRunResult();
    }

    // ── Test 1: IConfigureAeroDB discovery ──────────────────────────────────────

    [Test]
    public void Generator_discovers_IConfigureAeroDB_implementation()
    {
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureAeroDB
    {
        void Configure(StoreOptions options);
    }

    public class StoreOptions { }
}

public class MyConfigurator : AeroDB.IConfigureAeroDB
{
    public void Configure(AeroDB.StoreOptions options) { }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var generatedCode = result.GeneratedTrees[0].ToString();

        // Should contain AddDiscoveredAeroDBConfigurators method
        generatedCode.ShouldContain("AddDiscoveredAeroDBConfigurators");
        // Should register our configurator
        generatedCode.ShouldContain("MyConfigurator");
        // Should be in the right namespace
        generatedCode.ShouldContain("namespace AeroDB.Generated");
    }

    // ── Test 2: IAsyncConfigureAeroDB discovery ─────────────────────────────────

    [Test]
    public void Generator_discovers_IAsyncConfigureAeroDB_implementation()
    {
        var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using AeroDB;

namespace AeroDB
{
    public interface IAsyncConfigureAeroDB
    {
        Task ConfigureAsync(StoreOptions options, CancellationToken ct = default);
    }

    public class StoreOptions { }
}

public class MyAsyncConfigurator : AeroDB.IAsyncConfigureAeroDB
{
    public Task ConfigureAsync(AeroDB.StoreOptions options, CancellationToken ct) => Task.CompletedTask;
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var generatedCode = result.GeneratedTrees[0].ToString();
        generatedCode.ShouldContain("AddDiscoveredAeroDBConfigurators");
        generatedCode.ShouldContain("MyAsyncConfigurator");
        generatedCode.ShouldContain("IAsyncConfigureAeroDB");
    }

    // ── Test 3: Abstract class filtering ─────────────────────────────────────

    [Test]
    public void Generator_skips_abstract_classes()
    {
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureAeroDB { void Configure(StoreOptions options); }
    public class StoreOptions { }
}

public abstract class AbstractConfigurator : AeroDB.IConfigureAeroDB
{
    public abstract void Configure(AeroDB.StoreOptions options);
}

public class ConcreteConfigurator : AeroDB.IConfigureAeroDB
{
    public void Configure(AeroDB.StoreOptions options) { }
}
";
        var result = RunGenerator(source);
        var generatedCode = result.GeneratedTrees[0].ToString();

        // Should include ConcreteConfigurator but NOT AbstractConfigurator
        generatedCode.ShouldContain("ConcreteConfigurator");
        generatedCode.ShouldNotContain("AbstractConfigurator");
    }

    // ── Test 4: Empty result when no configurators ────────────────────────────

    [Test]
    public void Generator_empty_when_no_configurators()
    {
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureAeroDB { void Configure(StoreOptions options); }
    public class StoreOptions { }
}

public class NotAConfigurator { } // Does NOT implement IConfigureAeroDB
";
        var result = RunGenerator(source);
        var generatedCode = result.GeneratedTrees[0].ToString();

        // Should still generate the class but with empty registration
        generatedCode.ShouldContain("AddDiscoveredAeroDBConfigurators");
        generatedCode.ShouldContain("return services;"); // Just returns
        generatedCode.ShouldNotContain("AddSingleton"); // No registrations
    }

    // ── Test 5: Generated code compiles ──────────────────────────────────────

    [Test]
    public void Generator_output_compiles()
    {
        var source = @"
using System;
using AeroDB;

namespace AeroDB
{
    public interface IConfigureAeroDB { void Configure(StoreOptions options); }
    public class StoreOptions { }
}

public class TestConfig : AeroDB.IConfigureAeroDB
{
    public void Configure(AeroDB.StoreOptions options) { }
}
";
        var result = RunGenerator(source);
        var generatedCode = result.GeneratedTrees[0].ToString();

        // Compile the user source + generated code together so all type
        // references (e.g. global::AeroDB.IConfigureAeroDB) resolve correctly.
        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)),
            CSharpSyntaxTree.ParseText(generatedCode, new CSharpParseOptions(LanguageVersion.Latest)),
        };

        _ = typeof(IServiceCollection);

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name != "AeroDB" && name != "AeroDB.SourceGenerators";
            })
            .GroupBy(a => a.Location)
            .Select(g => g.First())
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location))
            .Cast<MetadataReference>()
            .DistinctBy(r => r.Display)
            .ToArray();

        var compilation = CSharpCompilation.Create("Generated",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        diagnostics.Count.ShouldBe(0, string.Join(Environment.NewLine, diagnostics));
    }
}
