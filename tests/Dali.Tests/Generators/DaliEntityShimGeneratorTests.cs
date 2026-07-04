using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Dali.SourceGenerators;
using TUnit.Core;

namespace Dali.Tests.Generators;

/// <summary>
/// Tests for <see cref="DaliEntityShimGenerator"/> — generates CBOR-compatible
/// shim <c>Record</c> types for <c>Entity&lt;TId&gt;</c> subclasses with
/// <c>ToEntity()</c> materialization.
/// </summary>
public class DaliEntityShimGeneratorTests
{
    /// <summary>
    /// Minimal inline definitions for Dali types that the shim generator
    /// discovers via <c>GetTypeByMetadataName</c>.
    /// </summary>
    private const string DaliTypes = @"
using System;
using System.Collections.Generic;

namespace Dali
{
    public interface IEntity<TId>
        where TId : notnull, IEquatable<TId>, IComparable<TId>
    {
        TId Id { get; set; }
    }

    public abstract class Entity<TId> : IEntity<TId>
        where TId : notnull, IEquatable<TId>, IComparable<TId>
    {
        public TId Id { get; set; } = default!;
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class DaliDocumentAttribute : Attribute
    {
        public bool SkipGeneration { get; set; }
    }
}

namespace Dali.Metadata
{
    public static class MetadataRegistry
    {
        public static void Register<T>(ITypeMetadata<T> metadata) { }
        public static void RegisterShimType<TEntity>(Type shimType) { }
    }

    // Minimal stub so generated code can compile
    public readonly record struct FieldSchema(string Name, string SurrealType, bool CanRead, bool CanWrite);

    public interface ITypeMetadata
    {
        string TableName { get; }
        bool HasTenantId { get; }
        bool HasVersion { get; }
        bool HasDocumentMetadata { get; }
        bool HasEventProjectionDispatch { get; }
        string? VersionFieldName { get; }
        Func<object, long>? GetVersionAccessor { get; }
        Action<object, long>? SetVersionAccessor { get; }
        Func<object, string?>? GetRecordIdAccessor { get; }
        IReadOnlyList<FieldSchema>? Fields { get; }
    }

    public interface ITypeMetadata<T> : ITypeMetadata
    {
        string? GetTenantId(T entity);
        long GetVersion(T entity);
        void SetVersion(T entity, long version);
        string? GetRecordId(T entity);
        void SetTenantId(T entity, string? tenantId);
    }
}
";

    /// <summary>
    /// Runs the <see cref="DaliEntityShimGenerator"/> in-process.
    /// </summary>
    private static GeneratorDriverRunResult RunGenerator(params string[] sources)
    {
        // Ensure SurrealDB assemblies are loaded
        _ = typeof(SurrealDb.Net.Models.Record);
        _ = typeof(SurrealDb.Net.Models.RecordIdOf<string>);

        var allSources = sources.Prepend(DaliTypes).ToArray();
        var syntaxTrees = allSources
            .Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest)))
            .ToArray();

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name != "Dali" && name != "Dali.SourceGenerators";
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

        // Fail fast if the compilation has errors — otherwise the generator
        // silently matches nothing because GetDeclaredSymbol returns null.
        var preErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (preErrors.Length > 0)
        {
            var messages = string.Join(Environment.NewLine, preErrors.Select(d => d.ToString()));
            throw new InvalidOperationException(
                $"Compilation has {preErrors.Length} error(s) before running the generator:{Environment.NewLine}{messages}");
        }

        var generator = new DaliEntityShimGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        return driver.RunGenerators(compilation).GetRunResult();
    }

    // ── Test 1: Entity<long> generates shim ──────────────────────────────────

    [Test]
    public void Entity_long_generates_shim()
    {
        var source = @"
public class MyLongEntity : Dali.Entity<long>
{
    public string Name { get; set; }
    public int Value { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("MyLongEntityShim");
        code.ShouldContain("class MyLongEntityShim : Record");
        code.ShouldContain("ToEntity()");
        code.ShouldContain("MetadataRegistry.RegisterShimType<global::MyLongEntity>");
        // Should handle long ID extraction
        code.ShouldContain("RecordIdOf<long>");
        code.ShouldContain("entity.Id = lr.Id");
    }

    // ── Test 2: Entity<string> generates shim ────────────────────────────────

    [Test]
    public void Entity_string_generates_shim()
    {
        var source = @"
public class MyStringEntity : Dali.Entity<string>
{
    public string Label { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("MyStringEntityShim");
        code.ShouldContain("RecordIdOf<string>");
        code.ShouldContain("entity.Id = sr.Id");
    }

    // ── Test 3: Entity<int> generates shim ───────────────────────────────────

    [Test]
    public void Entity_int_generates_shim()
    {
        var source = @"
public class MyIntEntity : Dali.Entity<int>
{
    public int Value { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("MyIntEntityShim");
        code.ShouldContain("RecordIdOf<int>");
        code.ShouldContain("entity.Id = ir.Id");
    }

    // ── Test 4: Entity<Guid> generates shim ──────────────────────────────────

    [Test]
    public void Entity_Guid_generates_shim()
    {
        var source = @"
using System;
public class MyGuidEntity : Dali.Entity<Guid>
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("MyGuidEntityShim");
        code.ShouldContain("Guid.Parse");
        code.ShouldContain("entity.Id = Guid.Empty");
    }

    // ── Test 5: Shim mirrors properties with CborProperty ────────────────────

    [Test]
    public void Shim_properties_have_CborProperty_attributes()
    {
        var source = @"
public class EntityWithProps : Dali.Entity<long>
{
    public string FirstName { get; set; }
    public int Age { get; set; }
    public bool IsActive { get; set; }
    public double Score { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        // Each property should have a CborProperty attribute
        code.ShouldContain("[CborProperty(\"firstName\")]");
        code.ShouldContain("[CborProperty(\"age\")]");
        code.ShouldContain("[CborProperty(\"isActive\")]");
        code.ShouldContain("[CborProperty(\"score\")]");

        // Properties should exist on the shim
        code.ShouldContain("public string FirstName { get; set; }");
        code.ShouldContain("public int Age { get; set; }");
    }

    // ── Test 6: ToEntity copies all properties ───────────────────────────────

    [Test]
    public void ToEntity_maps_all_properties()
    {
        var source = @"
public class EntityMapping : Dali.Entity<long>
{
    public string Title { get; set; }
    public int Rank { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        // Should contain property copies in ToEntity
        code.ShouldContain("entity.Title = this.Title;");
        code.ShouldContain("entity.Rank = this.Rank;");

        // Should create a new entity instance
        code.ShouldContain("var entity = new global::EntityMapping();");
    }

    // ── Test 7: SkipGeneration opt-out ───────────────────────────────────────

    [Test]
    public void SkipGeneration_opt_out_skips_shim()
    {
        var source = @"
[Dali.DaliDocument(SkipGeneration = true)]
public class SkippedEntity : Dali.Entity<long>
{
    public string Name { get; set; }
}

public class IncludedEntity : Dali.Entity<long>
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBe(1);
        var code = result.GeneratedTrees[0].ToString();
        code.ShouldContain("IncludedEntityShim");
        code.ShouldNotContain("SkippedEntity");
    }

    // ── Test 8: Abstract class is skipped ────────────────────────────────────

    [Test]
    public void Abstract_class_is_skipped()
    {
        var source = @"
public abstract class AbstractEntity : Dali.Entity<long>
{
    public string Name { get; set; }
}

public class ConcreteEntity : Dali.Entity<long>
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBe(1);
        var code = result.GeneratedTrees[0].ToString();
        code.ShouldContain("ConcreteEntityShim");
        code.ShouldNotContain("AbstractEntity");
    }

    // ── Test 9: Non-entity types produce no output ───────────────────────────

    [Test]
    public void Non_entity_types_produce_no_output()
    {
        var source = @"
public class NotAnEntity
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBe(0);
    }

    // ── Test 10: Generated code compiles ─────────────────────────────────────

    [Test]
    public void Generated_output_compiles()
    {
        var userSource = @"
using System;
using Dali;
using Dali.Metadata;

public class CompilableShimEntity : Dali.Entity<long>
{
    public string Name { get; set; }
    public int Count { get; set; }
}
";
        _ = typeof(SurrealDb.Net.Models.Record);
        _ = typeof(SurrealDb.Net.Models.RecordIdOf<string>);

        var allSources = new[] { DaliTypes, userSource };
        var syntaxTrees = allSources
            .Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest)))
            .ToArray();

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name != "Dali" && name != "Dali.SourceGenerators";
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

        var generator = new DaliEntityShimGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var genErrors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        genErrors.Count.ShouldBe(0);

        var compileErrors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        compileErrors.Count.ShouldBe(0);
    }
}
