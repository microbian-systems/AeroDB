using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using AeroDB.SourceGenerators;
using AeroDB.SourceGenerators;
using TUnit.Core;

namespace AeroDB.Tests.Generators;

/// <summary>
/// Tests for <see cref="AeroDBDocumentGenerator"/> — generates per-type metadata
/// classes implementing <c>ITypeMetadata&lt;T&gt;</c> for <c>Record</c> and
/// <c>SableDocument&lt;TId&gt;</c> subclasses.
/// </summary>
public class AeroDBDocumentGeneratorTests
{
    /// <summary>
    /// Minimal inline definitions for AeroDB.Sable types that the generator discovers
    /// via <c>GetTypeByMetadataName</c>. These are defined inline because the
    /// real <c>AeroDB.Sable</c> assembly is excluded from compilation references to
    /// avoid interface conflicts.
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

    public interface IVersioned { long Version { get; set; } }

    [AttributeUsage(AttributeTargets.Property)]
    public class VersionAttribute : Attribute { }

    public enum EncryptionAlgorithm
    {
        Aes256Gcm = 1,
        ChaCha20Poly1305 = 2
    }

    public enum BlindIndexAlgorithm
    {
        HmacSha256 = 1
    }

    public enum BlindIndexNormalizer
    {
        UsSocialSecurityNumberV1 = 1
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class EncryptAttribute : Attribute
    {
        public EncryptAttribute() { }
        public EncryptAttribute(EncryptionAlgorithm algorithm) { }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class BlindIndexAttribute : Attribute
    {
        public BlindIndexAlgorithm Algorithm { get; set; } = BlindIndexAlgorithm.HmacSha256;
        public BlindIndexNormalizer Normalizer { get; set; } = BlindIndexNormalizer.UsSocialSecurityNumberV1;
        public string? StorageFieldName { get; set; }
    }
}

namespace AeroDB.Sable.Metadata
{
    public readonly record struct FieldSchema(string Name, string SurrealType, bool CanRead, bool CanWrite)
    {
        public bool IsFlexible { get; init; }
        public AeroDB.Sable.EncryptionAlgorithm? EncryptionAlgorithm { get; init; }
    }

    public sealed record EncryptedFieldDescriptor(
        string PropertyName,
        Type ClrType,
        string CodecId,
        AeroDB.Sable.EncryptionAlgorithm Algorithm,
        Func<object, object?> GetValue,
        Action<object, object?> SetValue);

    public sealed record BlindIndexDescriptor(
        string PropertyName,
        AeroDB.Sable.BlindIndexAlgorithm Algorithm,
        AeroDB.Sable.BlindIndexNormalizer Normalizer,
        string? StorageFieldName,
        Func<object, string?> GetValue);

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
        IReadOnlyList<EncryptedFieldDescriptor>? EncryptedFields { get; }
        IReadOnlyList<BlindIndexDescriptor>? BlindIndexes { get; }
    }

    public interface ITypeMetadata<T> : ITypeMetadata
    {
        string? GetTenantId(T entity);
        long GetVersion(T entity);
        void SetVersion(T entity, long version);
        object? GetIdentity(T entity);
        void SetTenantId(T entity, string? tenantId);
    }

    public static class MetadataRegistry
    {
        public static void Register<T>(ITypeMetadata<T> metadata) { }
        public static void RegisterShimType<TEntity>(Type shimType) { }
    }

    public interface IDocumentMetadata
    {
        DateTimeOffset CreatedAt { get; set; }
        DateTimeOffset? LastModified { get; set; }
        string? LastModifiedBy { get; set; }
    }
}
";

    /// <summary>
    /// Runs the <see cref="AeroDBDocumentGenerator"/> in-process over the given
    /// C# source snippets combined with the inline AeroDB.Sable type definitions.
    /// </summary>
    private static GeneratorDriverRunResult RunGenerator(params string[] sources)
    {
        // Ensure SurrealDB assemblies are loaded into the AppDomain
        _ = typeof(SurrealDb.Net.Models.Record);
        _ = typeof(SurrealDb.Net.Models.RecordIdOf<string>);
        _ = typeof(System.ComponentModel.DataAnnotations.RequiredAttribute);

        var allSources = sources.Prepend(AeroDBTypes).ToArray();
        var syntaxTrees = allSources
            .Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest)))
            .ToArray();

        var referencePaths = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name != "AeroDB.Sable" && name != "AeroDB.Sable.SourceGenerators";
            })
            .Select(a => a.Location)
            .Append(typeof(System.ComponentModel.DataAnnotations.RequiredAttribute).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var references = referencePaths
            .Select(path => MetadataReference.CreateFromFile(path))
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

        var generator = new AeroDBDocumentGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        return driver.RunGenerators(compilation).GetRunResult();
    }

    // ── Test 1: Record subclass generates metadata ──────────────────────────

    [Test]
    public void Record_subclass_generates_metadata()
    {
        var source = @"
public class MyDocument : SurrealDb.Net.Models.Record
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("MyDocumentMetadata");
        code.ShouldContain("ITypeMetadata<global::MyDocument>");
        code.ShouldContain("TableName => \"my_document\"");
        code.ShouldContain("HasVersion => false");
        code.ShouldContain("HasTenantId => false");
        code.ShouldContain("HasDocumentMetadata => false");
    }

    // ── Test 2: SableDocument<long> subclass generates metadata ─────────────

    [Test]
    public void SableDocument_long_subclass_generates_metadata()
    {
        var source = @"
public class MyEntity : AeroDB.Sable.SableDocument<long>
{
    public string Label { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("MyEntityMetadata");
        code.ShouldContain("ITypeMetadata<global::MyEntity>");
        code.ShouldContain("TableName => \"my_entity\"");
        // SableDocument<long> metadata must preserve the native CLR identity type.
        code.ShouldContain("GetIdentity");
        code.ShouldContain("IdentityType => typeof(long)");
        code.ShouldContain("GetIdentity(global::MyEntity entity) => entity.Id;");
    }

    // ── Test 3: Properties generate correct FieldSchema entries ────────────

    [Test]
    public void Properties_generate_field_schema_entries()
    {
        var source = @"
using System;

public class DocumentWithProps : SurrealDb.Net.Models.Record
{
    public string Title { get; set; }
    public int Count { get; set; }
    public DateTime CreatedAt { get; set; }
    public double Score { get; set; }
    public bool IsActive { get; set; }
    public System.Collections.Generic.List<string> Tags { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("Title");
        code.ShouldContain("Count");
        code.ShouldContain("CreatedAt");
        code.ShouldContain("Score");
        code.ShouldContain("IsActive");
        code.ShouldContain("Tags");

        // Verify Surreal type mappings
        // Option B: reference types without [Required] emit option<T>
        code.ShouldContain("\"option<string>\"");
        code.ShouldContain("\"int\"");
        code.ShouldContain("\"datetime\"");
        code.ShouldContain("\"float\"");
        code.ShouldContain("\"bool\"");
        code.ShouldContain("\"option<array>\"");
    }

    [Test]
    public void Encrypt_attribute_generates_envelope_schema_and_typed_accessors()
    {
        var source = @"
public class ProtectedDocument : AeroDB.Sable.SableDocument<long>
{
    [AeroDB.Sable.Encrypt]
    public string Secret { get; set; } = """";

    [AeroDB.Sable.Encrypt(AeroDB.Sable.EncryptionAlgorithm.Aes256Gcm)]
    public byte[] Payload { get; set; } = System.Array.Empty<byte>();
}
";

        var result = RunGenerator(source);
        var code = result.GeneratedTrees
            .Single(tree => tree.FilePath.Contains("ProtectedDocument.Metadata", StringComparison.Ordinal))
            .ToString();

        code.ShouldContain(
            "FieldSchema(\"Secret\", \"option<object>\", true, true) { IsFlexible = true, EncryptionAlgorithm = global::AeroDB.Sable.EncryptionAlgorithm.Aes256Gcm }");
        code.ShouldContain("\"utf8-string-v1\"");
        code.ShouldContain("\"bytes-v1\"");
        code.ShouldContain("EncryptedFieldDescriptor");
        code.ShouldContain("obj => ((global::ProtectedDocument)obj).Secret");
    }

    [Test]
    public void Unsupported_encrypt_type_still_generates_fail_closed_metadata()
    {
        var source = @"
public class InvalidProtectedDocument : AeroDB.Sable.SableDocument<long>
{
    [AeroDB.Sable.Encrypt]
    public int SecretNumber { get; set; }
}
";

        var result = RunGenerator(source);
        var code = result.GeneratedTrees
            .Single(tree => tree.FilePath.Contains("InvalidProtectedDocument.Metadata", StringComparison.Ordinal))
            .ToString();

        code.ShouldContain("\"unsupported-v1\"");
        code.ShouldContain("EncryptedFieldDescriptor");
        code.ShouldContain("EncryptionAlgorithm.Aes256Gcm");
    }

    [Test]
    public void Encrypt_attribute_on_identity_still_generates_fail_closed_metadata()
    {
        var source = @"
public sealed class InvalidEncryptedIdentity : AeroDB.Sable.ISableDocument<string>
{
    [AeroDB.Sable.Encrypt]
    public string Id { get; set; } = """";
}
";

        var result = RunGenerator(source);
        var code = result.GeneratedTrees
            .Single(tree => tree.FilePath.Contains(
                "InvalidEncryptedIdentity.Metadata",
                StringComparison.Ordinal))
            .ToString();

        code.ShouldContain(
            "EncryptedFieldDescriptor(\"Id\", typeof(string), \"utf8-string-v1\"");
        code.ShouldContain("GetIdentityAccessor");
    }

    [Test]
    public void Blind_index_attribute_generates_keyed_lookup_metadata()
    {
        var source = @"
public class BlindIndexedDocument : AeroDB.Sable.SableDocument<long>
{
    [AeroDB.Sable.Encrypt]
    [AeroDB.Sable.BlindIndex(StorageFieldName = ""ssn_lookup"")]
    public string SocialSecurityNumber { get; set; } = """";
}
";

        var result = RunGenerator(source);
        var code = result.GeneratedTrees
            .Single(tree => tree.FilePath.Contains("BlindIndexedDocument.Metadata", StringComparison.Ordinal))
            .ToString();

        code.ShouldContain("BlindIndexDescriptor");
        code.ShouldContain("BlindIndexAlgorithm.HmacSha256");
        code.ShouldContain("BlindIndexNormalizer.UsSocialSecurityNumberV1");
        code.ShouldContain("\"ssn_lookup\"");
        code.ShouldContain("obj => (string?)(object?)((global::BlindIndexedDocument)obj).SocialSecurityNumber");
    }

    [Test]
    public void Embedded_poco_generates_flexible_object_field_schema()
    {
        var source = @"
public class MediaDocument : AeroDB.Sable.SableDocument<long>
{
    public MediaAttribution? Attribution { get; set; }
}

public sealed class MediaAttribution
{
    public string? CreatorName { get; set; }
}
";

        var result = RunGenerator(source);
        var code = result.GeneratedTrees
            .Single(tree => tree.FilePath.Contains("MediaDocument.Metadata", StringComparison.Ordinal))
            .ToString();

        code.ShouldContain(
            "new global::AeroDB.Sable.Metadata.FieldSchema(\"Attribution\", \"option<object>\", true, true) { IsFlexible = true }");
    }

    // ── Test 4: TenantId property detection ─────────────────────────────────

    [Test]
    public void TenantId_property_sets_HasTenantId_true()
    {
        var source = @"
public class TenantDocument : SurrealDb.Net.Models.Record
{
    public string? TenantId { get; set; }
    public string Data { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("HasTenantId => true");
        code.ShouldContain("GetTenantId(");
        code.ShouldContain("SetTenantId(");
    }

    // ── Test 5: IVersioned interface detection ──────────────────────────────

    [Test]
    public void IVersioned_interface_sets_HasVersion_true()
    {
        var source = @"
public class VersionedDoc : SurrealDb.Net.Models.Record, AeroDB.Sable.IVersioned
{
    public long Version { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("HasVersion => true");
        code.ShouldContain("VersionFieldName => \"Version\"");
        code.ShouldContain("GetVersion(");
        code.ShouldContain("SetVersion(");
    }

    // ── Test 6: [Version] attribute detection ────────────────────────────────

    [Test]
    public void VersionAttribute_on_property_sets_HasVersion_true()
    {
        var source = @"
public class AttrVersionDoc : SurrealDb.Net.Models.Record
{
    [AeroDB.Sable.Version]
    public long Revision { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("HasVersion => true");
        code.ShouldContain("VersionFieldName => \"Revision\"");
    }

    // ── Test 7: IDocumentMetadata detection ──────────────────────────────────

    [Test]
    public void IDocumentMetadata_sets_HasDocumentMetadata_true()
    {
        var source = @"
using AeroDB.Sable.Metadata;
public class AuditDoc : SurrealDb.Net.Models.Record, IDocumentMetadata
{
    public System.DateTimeOffset CreatedAt { get; set; }
    public System.DateTimeOffset? LastModified { get; set; }
    public string? LastModifiedBy { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("HasDocumentMetadata => true");
    }

    // ── Test 8: [AeroDBDocument(SkipGeneration = true)] opt-out ───────────────

    [Test]
    public void SkipGeneration_opt_out_skips_type()
    {
        var source = @"
[AeroDB.Sable.AeroDBDocument(SkipGeneration = true)]
public class SkippedDoc : SurrealDb.Net.Models.Record
{
    public string Name { get; set; }
}

public class IncludedDoc : SurrealDb.Net.Models.Record
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(source);

        // Only one generated tree (for IncludedDoc)
        result.GeneratedTrees.Length.ShouldBe(1);
        var code = result.GeneratedTrees[0].ToString();
        code.ShouldContain("IncludedDocMetadata");
        code.ShouldNotContain("SkippedDoc");
    }

    // ── Test 9: Abstract class filtering ─────────────────────────────────────

    [Test]
    public void Abstract_class_is_skipped()
    {
        var source = @"
public abstract class AbstractDoc : SurrealDb.Net.Models.Record
{
    public string Name { get; set; }
}

public class ConcreteDoc : SurrealDb.Net.Models.Record
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBe(1);
        var code = result.GeneratedTrees[0].ToString();
        code.ShouldContain("ConcreteDocMetadata");
        code.ShouldNotContain("AbstractDoc");
    }

    // ── Test 10: Multiple documents generate multiple outputs ───────────────

    [Test]
    public void Multiple_documents_generate_separate_metadata_files()
    {
        var source = @"
public class FirstDoc : SurrealDb.Net.Models.Record
{
    public string A { get; set; }
}

public class SecondDoc : SurrealDb.Net.Models.Record
{
    public int B { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBe(2);

        var tree0 = result.GeneratedTrees[0].ToString();
        var tree1 = result.GeneratedTrees[1].ToString();
        var allNames = new[] { tree0, tree1 }.SelectMany(t => new[]
        {
            t.Contains("FirstDocMetadata") ? "FirstDoc" : null,
            t.Contains("SecondDocMetadata") ? "SecondDoc" : null
        }).Where(n => n is not null).ToList();

        allNames.ShouldContain("FirstDoc");
        allNames.ShouldContain("SecondDoc");
    }

    // ── Test 11: No matching types produces no output ───────────────────────

    [Test]
    public void No_matching_types_produces_no_output()
    {
        var source = @"
public class NotAMatch { }
public class AlsoNot { public string X { get; set; } }
";
        var result = RunGenerator(source);

        // Generator returns zero generated trees when no Record/SableDocument subclass found
        result.GeneratedTrees.Length.ShouldBe(0);
    }

    // ── Test 12: Generated code compiles successfully ──────────────────────

    [Test]
    public void Generated_output_compiles()
    {
        var userSource = @"
using System;
using AeroDB.Sable;
using AeroDB.Sable.Metadata;

public class CompilableDoc : SurrealDb.Net.Models.Record
{
    public string Name { get; set; }
    public int Count { get; set; }
}
";

        // Get references (same as RunGenerator, but we need to compile
        // the user source + generated code together)
        _ = typeof(SurrealDb.Net.Models.Record);
        _ = typeof(SurrealDb.Net.Models.RecordIdOf<string>);

        // Combine AeroDBTypes + userSource for the generator pass
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
            .GroupBy(a => a.Location)
            .Select(g => MetadataReference.CreateFromFile(g.Key))
            .Cast<MetadataReference>()
            .ToArray();

        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        // Run the generator and get the output compilation
        var generator = new AeroDBDocumentGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        // Generator diagnostics should have no errors
        var genErrors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        genErrors.Count.ShouldBe(0);

        // The output compilation (which includes generated trees) should compile without errors
        var compileErrors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        compileErrors.Count.ShouldBe(0);
    }

    // ── Test 13: SableDocument<long> with TenantId and Version ──────────────

    [Test]
    public void SableDocument_with_tenant_and_version_detects_both()
    {
        var source = @"
public class FullEntity : AeroDB.Sable.SableDocument<long>, AeroDB.Sable.IVersioned
{
    public string? TenantId { get; set; }
    public long Version { get; set; }
    public string Payload { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("HasVersion => true");
        code.ShouldContain("HasTenantId => true");
        code.ShouldContain("VersionFieldName => \"Version\"");
        code.ShouldContain("GetTenantId(");
        code.ShouldContain("GetVersion(");
    }

    // ── Test 14: Reference type string emits option<string> ──────────────────

    [Test]
    public void Reference_type_string_emits_option_string()
    {
        var source = @"
public class StringDoc : SurrealDb.Net.Models.Record
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        // Option B: non-required reference types get option<T>
        code.ShouldContain("\"option<string>\"");
        code.ShouldNotContain("\"string\"");
    }

    // ── Test 15: [Required] string emits bare string ────────────────────────

    [Test]
    public void Required_string_attribute_emits_bare_string()
    {
        var source = @"
using System.ComponentModel.DataAnnotations;

public class RequiredDoc : SurrealDb.Net.Models.Record
{
    [Required]
    public string Title { get; set; }
    public string Description { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        // [Required] overrides Option B — emits bare type
        code.ShouldContain("\"string\"");             // Title has [Required]
        code.ShouldContain("\"option<string>\"");     // Description does not
    }

    // ── Test 16: Nullable reference type emits option<string> ───────────────

    [Test]
    public void Nullable_reference_type_emits_option_string()
    {
        var source = @"
using System;

public class NullableRefDoc : SurrealDb.Net.Models.Record
{
    public string? Bio { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("\"option<string>\"");
    }

    // ── Test 17: Value types unchanged by Option B ──────────────────────────

    [Test]
    public void Value_types_unchanged_by_option_b()
    {
        var source = @"
using System;

public class ValueTypeDoc : SurrealDb.Net.Models.Record
{
    public int Count { get; set; }
    public long Id { get; set; }
    public bool IsActive { get; set; }
    public double Price { get; set; }
    public DateTime CreatedAt { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        // Value types are never nullable by default — bare surreal types
        code.ShouldContain("\"int\"");
        code.ShouldContain("\"bool\"");
        code.ShouldContain("\"float\"");
        code.ShouldContain("\"datetime\"");
    }

    // ── Test 18: Nullable value type emits option<int> ──────────────────────

    [Test]
    public void Nullable_value_type_emits_option_int()
    {
        var source = @"
public class NullableValueDoc : SurrealDb.Net.Models.Record
{
    public int? Age { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        code.ShouldContain("\"option<int>\"");
    }

    // ── Test 19: [Required] on nullable reference type overrides option ─────

    [Test]
    public void Required_on_nullable_reference_type_overrides_option()
    {
        var source = @"
using System.ComponentModel.DataAnnotations;

public class RequiredNullableDoc : SurrealDb.Net.Models.Record
{
    [Required]
    public string? Name { get; set; }
}
";
        var result = RunGenerator(source);

        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();

        // [Required] overrides both Option B and nullable annotation
        code.ShouldContain("\"string\"");
        code.ShouldNotContain("\"option<string>\"");
    }

    // ── Test 20: Record subclass collection types map to array ────────────────

    [Test]
    public void Record_subclass_collection_types_map_to_array()
    {
        var source = @"
using System.Collections.Generic;
public class RecordWithCollections : SurrealDb.Net.Models.Record
{
    public IList<string> StringList { get; set; } = new List<string>();
    public ICollection<int> IntCollection { get; set; } = new List<int>();
    public IReadOnlyList<long> LongReadOnlyList { get; set; } = new List<long>();
    public IReadOnlyCollection<string> StringReadOnlyCollection { get; set; } = new List<string>();
    public ISet<int> IntSet { get; set; } = new HashSet<int>();
    public List<string> PlainStringList { get; set; } = new List<string>();
}
";
        var result = RunGenerator(source);
        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();
        
        code.ShouldContain("\"option<array>\"");
    }

    // ── Test 21: SableDocument subclass collection types map to array ─────────

    [Test]
    public void SableDocument_subclass_collection_types_map_to_array()
    {
        var source = @"
using AeroDB.Sable;
using System.Collections.Generic;
public class SableWithCollections : SableDocument<long>
{
    public IList<string> StringList { get; set; } = new List<string>();
    public ICollection<int> IntCollection { get; set; } = new List<int>();
    public IReadOnlyList<long> LongReadOnlyList { get; set; } = new List<long>();
    public IReadOnlyCollection<string> StringReadOnlyCollection { get; set; } = new List<string>();
    public ISet<int> IntSet { get; set; } = new HashSet<int>();
    public List<string> PlainStringList { get; set; } = new List<string>();
}
";
        var result = RunGenerator(source);
        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();
        
        code.ShouldContain("\"option<array>\"");
    }

    // ── Test 22: ISableDocument implementor collection types map to array ─────

    [Test]
    public void ISableDocument_implementor_collection_types_map_to_array()
    {
        var source = @"
using AeroDB.Sable;
using System.Collections.Generic;
public class InterfaceWithCollections : ISableDocument<long>
{
    public long Id { get; set; }
    public IList<string> StringList { get; set; } = new List<string>();
    public ICollection<int> IntCollection { get; set; } = new List<int>();
    public IReadOnlyList<long> LongReadOnlyList { get; set; } = new List<long>();
    public IReadOnlyCollection<string> StringReadOnlyCollection { get; set; } = new List<string>();
    public ISet<int> IntSet { get; set; } = new HashSet<int>();
    public List<string> PlainStringList { get; set; } = new List<string>();
}
";
        var result = RunGenerator(source);
        result.GeneratedTrees.Length.ShouldBeGreaterThan(0);
        var code = result.GeneratedTrees[0].ToString();
        
        code.ShouldContain("\"option<array>\"");
    }

    // ── Test 23: Plain POCO with collection types skipped ────────────────────

    [Test]
    public void Plain_POCO_with_collection_types_skipped()
    {
        var source = @"
using System.Collections.Generic;
public class PlainPoco
{
    public string Name { get; set; }
    public IList<string> StringList { get; set; } = new List<string>();
    public ISet<int> IntSet { get; set; } = new HashSet<int>();
}
";
        var result = RunGenerator(source);
        result.GeneratedTrees.Length.ShouldBe(0);
    }
}
