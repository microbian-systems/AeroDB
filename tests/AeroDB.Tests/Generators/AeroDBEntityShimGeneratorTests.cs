using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;
using Dahomey.Cbor.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using AeroDB.Sable;
using AeroDB.Sable.Metadata;
using AeroDB.SourceGenerators;
using AeroDB.SourceGenerators;
using TUnit.Core;

namespace AeroDB.Tests.Generators;

/// <summary>
/// Tests for <see cref="AeroDBEntityShimGenerator"/> — generates CBOR-compatible
/// shim <c>Record</c> types for <c>SableDocument&lt;TId&gt;</c> subclasses with
/// <c>ToEntity()</c> materialization.
/// </summary>
public class AeroDBEntityShimGeneratorTests
{
    /// <summary>
    /// Minimal inline definitions for AeroDB.Sable types that the shim generator
    /// discovers via <c>GetTypeByMetadataName</c>.
    /// </summary>
    private const string AeroDBTypes = @"
using System;
using System.Collections.Generic;

namespace AeroDB.Sable
{
    public interface ISableDocument<TId>
        where TId : notnull, IEquatable<TId>, IComparable<TId>
    {
        TId Id { get; set; }
    }

    public abstract class SableDocument<TId> : ISableDocument<TId>
        where TId : notnull, IEquatable<TId>, IComparable<TId>
    {
        public TId Id { get; set; } = default!;
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class AeroDBDocumentAttribute : Attribute
    {
        public bool SkipGeneration { get; set; }
    }
}

namespace AeroDB.Sable.Metadata
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
        Type? IdentityType { get; }
        Func<object, object?>? GetIdentityAccessor { get; }
        IReadOnlyList<FieldSchema>? Fields { get; }
    }

    public interface ITypeMetadata<T> : ITypeMetadata
    {
        string? GetTenantId(T entity);
        long GetVersion(T entity);
        void SetVersion(T entity, long version);
        object? GetIdentity(T entity);
        void SetTenantId(T entity, string? tenantId);
    }
}
";

    /// <summary>
    /// Runs the <see cref="AeroDBEntityShimGenerator"/> in-process.
    /// </summary>
    private static GeneratorDriverRunResult RunGenerator(params string[] sources)
    {
        // Ensure SurrealDB assemblies are loaded
        _ = typeof(SurrealDb.Net.Models.Record);
        _ = typeof(SurrealDb.Net.Models.RecordIdOf<string>);
        _ = typeof(CborPropertyAttribute);

        var allSources = sources.Prepend(AeroDBTypes).ToArray();
        var syntaxTrees = allSources
            .Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest)))
            .ToArray();

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name != "AeroDB.Sable" && name != "AeroDB.Sable.SourceGenerators";
            })
            .Append(typeof(CborPropertyAttribute).Assembly)
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

        var generator = new AeroDBEntityShimGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        return driver.RunGenerators(compilation).GetRunResult();
    }

    // ── Test 1: SableDocument<long> generates shim ────────────────────────────

    [Test]
    public void Entity_long_generates_shim()
    {
        var source = @"
public class MyLongEntity : AeroDB.Sable.SableDocument<long>
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

    // ── Test 2: SableDocument<string> generates shim ──────────────────────────

    [Test]
    public void Entity_string_generates_shim()
    {
        var source = @"
public class MyStringEntity : AeroDB.Sable.SableDocument<string>
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

    // ── Test 3: SableDocument<int> generates shim ─────────────────────────────

    [Test]
    public void Entity_int_generates_shim()
    {
        var source = @"
public class MyIntEntity : AeroDB.Sable.SableDocument<int>
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

    // ── Test 4: SableDocument<Guid> generates shim ────────────────────────────

    [Test]
    public void Entity_Guid_generates_shim()
    {
        var source = @"
using System;
public class MyGuidEntity : AeroDB.Sable.SableDocument<Guid>
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
public class EntityWithProps : AeroDB.Sable.SableDocument<long>
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
public class EntityMapping : AeroDB.Sable.SableDocument<long>
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
[AeroDB.Sable.AeroDBDocument(SkipGeneration = true)]
public class SkippedEntity : AeroDB.Sable.SableDocument<long>
{
    public string Name { get; set; }
}

public class IncludedEntity : AeroDB.Sable.SableDocument<long>
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
public abstract class AbstractEntity : AeroDB.Sable.SableDocument<long>
{
    public string Name { get; set; }
}

public class ConcreteEntity : AeroDB.Sable.SableDocument<long>
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
using AeroDB.Sable;
using AeroDB.Sable.Metadata;

public class CompilableShimEntity : AeroDB.Sable.SableDocument<long>
{
    public string Name { get; set; }
    public int Count { get; set; }
}
";
        _ = typeof(SurrealDb.Net.Models.Record);
        _ = typeof(SurrealDb.Net.Models.RecordIdOf<string>);
        _ = typeof(CborPropertyAttribute);

        var allSources = new[] { AeroDBTypes, userSource };
        var syntaxTrees = allSources
            .Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest)))
            .ToArray();

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name != "AeroDB.Sable" && name != "AeroDB.Sable.SourceGenerators";
            })
            .Append(typeof(CborPropertyAttribute).Assembly)
            .GroupBy(a => a.Location)
            .Select(g => MetadataReference.CreateFromFile(g.Key))
            .Cast<MetadataReference>()
            .ToArray();

        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        var generator = new AeroDBEntityShimGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var genErrors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        genErrors.Count.ShouldBe(0);

        var compileErrors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        compileErrors.Count.ShouldBe(0);
    }

    // ── Generated materialization regressions ───────────────────────────────

    [Test]
    public void Generated_shim_preserves_inherited_audit_version_array_and_snake_case_properties()
    {
        var source = @"
using System;

public abstract class AuditedDocument<TId> : AeroDB.Sable.SableDocument<TId>
    where TId : notnull, IEquatable<TId>, IComparable<TId>
{
    public long Version { get; set; }
    public DateTimeOffset CreatedOn { get; set; }
    public DateTimeOffset ModifiedOn { get; set; }
    public string? CreatedBy { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class ContractAttachmentDocument : AuditedDocument<long>
{
    public byte[] AttachmentBytes { get; set; } = [];
    public string agency_code { get; set; } = string.Empty;
}
";

        var result = RunGenerator(source);
        var shim = GetShimClass(result, "ContractAttachmentDocumentShim");

        AssertCborMappedProperty(shim, "Version", "version", "long");
        AssertCborMappedProperty(shim, "CreatedOn", "createdOn", "global::System.DateTimeOffset");
        AssertCborMappedProperty(shim, "ModifiedOn", "modifiedOn", "global::System.DateTimeOffset");
        AssertCborMappedProperty(shim, "CreatedBy", "createdBy", "string");
        AssertCborMappedProperty(shim, "ModifiedBy", "modifiedBy", "string");
        AssertCborMappedProperty(shim, "AttachmentBytes", "attachmentBytes", "byte[]");
        AssertCborMappedProperty(shim, "agency_code", "agency_code", "string");

        var materializer = shim.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "ToEntity");

        materializer.Body!.Statements
            .OfType<ExpressionStatementSyntax>()
            .Select(statement => statement.Expression)
            .OfType<AssignmentExpressionSyntax>()
            .Select(assignment => assignment.Left.ToString())
            .ShouldContain("entity.Version");
    }

    [Test]
    [Arguments("Version", "version", "long")]
    [Arguments("CreatedOn", "createdOn", "global::System.DateTimeOffset")]
    [Arguments("ModifiedOn", "modifiedOn", "global::System.DateTimeOffset")]
    [Arguments("CreatedBy", "createdBy", "string")]
    [Arguments("ModifiedBy", "modifiedBy", "string")]
    public void Generated_shim_preserves_each_inherited_audit_property(
        string propertyName,
        string cborName,
        string propertyType)
    {
        var source = @"
using System;

public abstract class AuditedDocument<TId> : AeroDB.Sable.SableDocument<TId>
    where TId : notnull, IEquatable<TId>, IComparable<TId>
{
    public long Version { get; set; }
    public DateTimeOffset CreatedOn { get; set; }
    public DateTimeOffset ModifiedOn { get; set; }
    public string? CreatedBy { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class AuditedContractDocument : AuditedDocument<long>
{
    public string Name { get; set; } = string.Empty;
}
";

        var shim = GetShimClass(RunGenerator(source), "AuditedContractDocumentShim");

        AssertCborMappedProperty(shim, propertyName, cborName, propertyType);
    }

    [Test]
    public void Generated_shim_compiles_with_a_module_initialized_registration()
    {
        var source = @"
public sealed class RegisteredMaterializationDocument : AeroDB.Sable.SableDocument<long>
{
    public string Name { get; set; } = string.Empty;
}
";

        var (result, outputCompilation, generatorDiagnostics) = RunGeneratorAndUpdateCompilation(source);
        generatorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();
        outputCompilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();

        var shim = GetShimClass(result, "RegisteredMaterializationDocumentShim");
        var initializer = shim.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => HasModuleInitializerAttribute(method));

        initializer.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)).ShouldBeTrue();
        initializer.ParameterList.Parameters.ShouldBeEmpty();
        initializer.ReturnType.ToString().ShouldBe("void");

        var registration = initializer.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(invocation => IsRegisterShimTypeInvocation(invocation));
        var genericName = (GenericNameSyntax)((MemberAccessExpressionSyntax)registration.Expression).Name;

        genericName.TypeArgumentList.Arguments.Single().ToString()
            .ShouldBe("global::RegisteredMaterializationDocument");
        registration.ArgumentList.Arguments.Single().Expression.ShouldBeOfType<TypeOfExpressionSyntax>()
            .Type.ToString().ShouldBe("RegisteredMaterializationDocumentShim");
    }

    [Test]
    public void Generated_materializer_maps_a_real_inherited_document_without_legacy_shim_activation()
    {
        var rows = new Dictionary<string, object?>
        {
            ["id"] = "generated_reader_fixture:9223372036854775000",
            ["version"] = 7L,
            ["created_on"] = DateTimeOffset.Parse("2026-10-11T15:00:00+00:00"),
            ["attachment_bytes"] = new byte[] { 7, 8 },
            ["embedded_document"] = new Dictionary<string, object?> { ["id"] = "alpha:beta" },
            ["line_items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["id"] = "alpha:beta", ["code"] = "SAM", ["quantity"] = 3 }
            }
        };

        MetadataRegistry.TryMaterialize<GeneratedReaderFixture>(
            rows,
            schema: null,
            EnumStorage.AsString,
            out var document).ShouldBeTrue();

        document.Id.ShouldBe(9223372036854775000L);
        document.Version.ShouldBe(7L);
        document.CreatedOn.Offset.ShouldBe(TimeSpan.Zero);
        document.AttachmentBytes.ShouldBe(new byte[] { 7, 8 });
        document.EmbeddedDocument!.Id.ShouldBe("alpha:beta");
        document.LineItems.Single().Code.ShouldBe("SAM");
        document.LineItems.Single().Id.ShouldBe("alpha:beta");
        document.LineItems.Single().Quantity.ShouldBe(3);
    }

    [Test]
    public void Generated_materializer_uses_a_strict_scalar_contract()
    {
        Should.Throw<InvalidOperationException>(() => MetadataRegistry.ReadScalar<int>("3", EnumStorage.AsString));
        Should.Throw<InvalidOperationException>(() => MetadataRegistry.ReadScalar<int>(3.5d, EnumStorage.AsString));
        Should.Throw<InvalidOperationException>(() => MetadataRegistry.ReadScalar<GeneratedUnsignedState>(1.5d, EnumStorage.AsString));
        Should.Throw<InvalidOperationException>(() => MetadataRegistry.ReadScalar<GeneratedUnsignedState>("Maximum", EnumStorage.AsInteger));

        MetadataRegistry.ReadScalar<GeneratedUnsignedState>(ulong.MaxValue, EnumStorage.AsString)
            .ShouldBe(GeneratedUnsignedState.Maximum);
        MetadataRegistry.ReadScalar<decimal>("4000.25", EnumStorage.AsString).ShouldBe(4000.25m);
    }

    [Test]
    public void Generated_materializer_keeps_json_ignored_defaults_when_raw_rows_contain_the_field()
    {
        MetadataRegistry.TryMaterialize<GeneratedIgnoredMemberFixture>(
            new Dictionary<string, object?>
            {
                ["id"] = "generated_ignored_member_fixture:17",
                ["name"] = "visible",
                ["ignored"] = new Dictionary<string, object?> { ["untrusted"] = true }
            },
            schema: null,
            EnumStorage.AsString,
            out var document).ShouldBeTrue();

        document.Id.ShouldBe(17L);
        document.Name.ShouldBe("visible");
        document.Ignored.ShouldBe("default value");
    }

    [Test]
    public void Generated_materializer_emits_metadata_only_generated_and_dispatch_miss_events()
    {
        var events = new List<string>();
        using var subscription = MetadataRegistry.GeneratedMaterializationDiagnostics.Subscribe(
            new DiagnosticEventObserver((name, _) => events.Add(name)));

        var generated = InternalSessionBase.DeserializePocoFromList<GeneratedReaderFixture>(
            [new Dictionary<string, object?> { ["id"] = "generated_reader_fixture:1" }]);
        generated.Single().Id.ShouldBe(1L);

        MetadataRegistry.TryMaterialize<GeneratedUnsupportedMemberFixture>(
            new Dictionary<string, object?>(),
            schema: null,
            EnumStorage.AsString,
            out _).ShouldBeFalse();

        events.ShouldContain("generated");
        events.ShouldNotContain("legacy");
        events.ShouldContain("generated-dispatch-miss");
    }

    [Test]
    public void Generated_materializer_distinguishes_missing_and_null_references_and_rejects_wrong_shapes()
    {
        var presentNull = new Dictionary<string, object?>
        {
            ["id"] = "generated_reader_fixture:3",
            ["line_items"] = null,
            ["primary_line"] = null
        };

        MetadataRegistry.TryMaterialize<GeneratedReaderFixture>(presentNull, null, EnumStorage.AsString, out var nullDocument)
            .ShouldBeTrue();
        nullDocument.LineItems.ShouldBeNull();
        nullDocument.PrimaryLine.ShouldBeNull();

        var malformed = new Dictionary<string, object?>
        {
            ["id"] = "generated_reader_fixture:4",
            ["line_items"] = "not a sequence"
        };

        Should.Throw<InvalidOperationException>(() =>
            MetadataRegistry.TryMaterialize<GeneratedReaderFixture>(malformed, null, EnumStorage.AsString, out _));
    }

    [Test]
    public void Generated_materializer_honors_configured_storage_names()
    {
        var schema = new SchemaOptions();
        schema.For<GeneratedIgnoredMemberFixture>().FieldName(document => document.Name, "contract_name");

        MetadataRegistry.TryMaterialize<GeneratedIgnoredMemberFixture>(
            new Dictionary<string, object?>
            {
                ["id"] = "generated_ignored_member_fixture:21",
                ["contract_name"] = "mapped name"
            },
            schema,
            EnumStorage.AsString,
            out var document).ShouldBeTrue();

        document.Name.ShouldBe("mapped name");
    }

    [Test]
    public void Generated_materializer_preserves_colons_in_string_record_identity_keys()
    {
        MetadataRegistry.TryMaterialize<GeneratedStringIdentityFixture>(
            new Dictionary<string, object?> { ["id"] = "generated_string_identity_fixture:alpha:beta" },
            null,
            EnumStorage.AsString,
            out var document).ShouldBeTrue();

        document.Id.ShouldBe("alpha:beta");
    }

    [Test]
    public void Generated_materializer_allows_missing_root_identity_but_rejects_a_present_null_identity()
    {
        MetadataRegistry.TryMaterialize<GeneratedStringIdentityFixture>(
            new Dictionary<string, object?>(),
            null,
            EnumStorage.AsString,
            out var missingIdentity).ShouldBeTrue();
        missingIdentity.Id.ShouldBeNull();

        Should.Throw<InvalidOperationException>(() => MetadataRegistry.TryMaterialize<GeneratedStringIdentityFixture>(
            new Dictionary<string, object?> { ["id"] = null },
            null,
            EnumStorage.AsString,
            out _));
    }

    [Test]
    public void Generated_materializer_falls_back_for_unsupported_and_required_shapes()
    {
        MetadataRegistry.TryMaterialize<GeneratedUnsupportedMemberFixture>(new Dictionary<string, object?>(), null, EnumStorage.AsString, out _)
            .ShouldBeFalse();
        MetadataRegistry.TryMaterialize<GeneratedPrivateSetterFixture>(new Dictionary<string, object?>(), null, EnumStorage.AsString, out _)
            .ShouldBeFalse();
        MetadataRegistry.TryMaterialize<GeneratedRequiredFixture>(new Dictionary<string, object?>(), null, EnumStorage.AsString, out _)
            .ShouldBeFalse();
    }

    [Test]
    public void Generated_materializer_does_not_run_for_an_explicit_non_id_identity()
    {
        var events = new List<string>();
        using var subscription = MetadataRegistry.GeneratedMaterializationDiagnostics.Subscribe(
            new DiagnosticEventObserver((name, _) => events.Add(name)));

        var result = InternalSessionBase.DeserializePocoFromList<GeneratedCustomIdentityFixture>(
            [new Dictionary<string, object?>
            {
                ["id"] = "generated_custom_identity_fixture:external-identity",
                ["name"] = "legacy compatible"
            }],
            identityProperty: nameof(GeneratedCustomIdentityFixture.ExternalKey));

        result.Single().ExternalKey.ShouldBe("external-identity");
        events.ShouldNotContain("generated");
        events.ShouldContain("legacy");
    }

    [Test]
    public void Generated_materializer_falls_back_for_a_document_with_a_converter_annotated_enum()
    {
        MetadataRegistry.TryMaterialize<GeneratedConverterEnumFixture>(
            new Dictionary<string, object?>(),
            null,
            EnumStorage.AsString,
            out _).ShouldBeFalse();
    }

    [Test]
    public void Generated_materializer_falls_back_for_a_conditionally_ignored_supported_member()
    {
        MetadataRegistry.TryMaterialize<GeneratedConditionalIgnoreFixture>(
            new Dictionary<string, object?>(),
            null,
            EnumStorage.AsString,
            out _).ShouldBeFalse();
    }

    private static (GeneratorDriverRunResult Result, Compilation OutputCompilation, ImmutableArray<Diagnostic> Diagnostics)
        RunGeneratorAndUpdateCompilation(params string[] sources)
    {
        _ = typeof(SurrealDb.Net.Models.Record);
        _ = typeof(SurrealDb.Net.Models.RecordIdOf<string>);
        _ = typeof(CborPropertyAttribute);

        var syntaxTrees = sources.Prepend(AeroDBTypes)
            .Select(source => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)))
            .ToArray();

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            .Where(assembly =>
            {
                var name = assembly.GetName().Name;
                return name != "AeroDB.Sable" && name != "AeroDB.Sable.SourceGenerators";
            })
            .Append(typeof(CborPropertyAttribute).Assembly)
            .GroupBy(assembly => assembly.Location)
            .Select(group => MetadataReference.CreateFromFile(group.Key))
            .Cast<MetadataReference>()
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new AeroDBEntityShimGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        var result = driver.RunGenerators(compilation).GetRunResult();

        return (result, outputCompilation, diagnostics);
    }

    private static ClassDeclarationSyntax GetShimClass(GeneratorDriverRunResult result, string shimName)
        => result.GeneratedTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Single(@class => @class.Identifier.ValueText == shimName);

    private static void AssertCborMappedProperty(
        ClassDeclarationSyntax shim,
        string propertyName,
        string expectedCborName,
        string expectedType)
    {
        shim.Members.OfType<PropertyDeclarationSyntax>()
            .Select(candidate => candidate.Identifier.ValueText)
            .ShouldContain(propertyName);

        var property = shim.Members.OfType<PropertyDeclarationSyntax>()
            .Single(candidate => candidate.Identifier.ValueText == propertyName);

        property.Type.ToString().ShouldBe(expectedType);
        property.AttributeLists.SelectMany(attributeList => attributeList.Attributes)
            .Single(attribute => attribute.Name.ToString() == "CborProperty")
            .ArgumentList!.Arguments.Single().Expression.ToString()
            .ShouldBe($"\"{expectedCborName}\"");
    }

    private static bool HasModuleInitializerAttribute(MethodDeclarationSyntax method)
        => method.AttributeLists.SelectMany(attributeList => attributeList.Attributes)
            .Any(attribute => attribute.Name.ToString().EndsWith("ModuleInitializer", StringComparison.Ordinal));

    private static bool IsRegisterShimTypeInvocation(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax
        {
            Name: GenericNameSyntax { Identifier.ValueText: "RegisterShimType" }
        };
}

file sealed class DiagnosticEventObserver(Action<string, object?> onEvent) : IObserver<KeyValuePair<string, object?>>
{
    public void OnCompleted() { }
    public void OnError(Exception error) => throw error;
    public void OnNext(KeyValuePair<string, object?> value) => onEvent(value.Key, value.Value);
}

public abstract class GeneratedReaderAuditedDocument<TId> : SableDocument<TId>
    where TId : notnull, IEquatable<TId>, IComparable<TId>
{
    public long Version { get; set; }
    public DateTimeOffset CreatedOn { get; set; }
}

public sealed class GeneratedReaderFixture : GeneratedReaderAuditedDocument<long>
{
    public byte[] AttachmentBytes { get; set; } = [];
    public List<GeneratedReaderLine> LineItems { get; set; } = [];
    public GeneratedReaderLine? PrimaryLine { get; set; }
    public EmbeddedGeneratedDocument? EmbeddedDocument { get; set; }
}

public sealed class GeneratedReaderLine
{
    public string? Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public enum GeneratedUnsignedState : ulong
{
    Maximum = ulong.MaxValue
}

public sealed class GeneratedIgnoredMemberFixture : SableDocument<long>
{
    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public object Ignored { get; set; } = "default value";
}

public sealed class GeneratedUnsupportedMemberFixture : SableDocument<long>
{
    public object Unsupported { get; set; } = new();
}

public sealed class GeneratedPrivateSetterFixture : SableDocument<long>
{
    public string Persisted { get; private set; } = string.Empty;
}

public sealed class GeneratedRequiredFixture : SableDocument<long>
{
    public required string Name { get; set; }
}

public sealed class GeneratedCustomIdentityFixture : SableDocument<long>
{
    public string ExternalKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class GeneratedStringIdentityFixture : SableDocument<string>
{
}

public sealed class EmbeddedGeneratedDocument : SableDocument<string>
{
}

public sealed class GeneratedConverterEnumFixture : SableDocument<long>
{
    public GeneratedConverterState State { get; set; }
}

public sealed class GeneratedConditionalIgnoreFixture : SableDocument<long>
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ConditionallyPersisted { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedConverterState
{
    Unknown,
    Ready
}
