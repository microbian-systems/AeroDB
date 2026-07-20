using AeroDB.Analyzers;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.CSharp.Testing;

namespace AeroDB.Analyzers.Tests;

public class EncryptedFieldQueryAnalyzerTests
{
    private static Task VerifyAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<EncryptedFieldQueryAnalyzer, DefaultVerifier>
        {
            TestCode = Stubs + source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80
        };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync(CancellationToken.None);
    }

    private const string Stubs = """
using System;
using System.Linq;
using System.Collections.Generic;

namespace AeroDB.Sable
{
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
    }

    public sealed class DocumentMapping<T>
    {
        public DocumentMapping<T> Identity<TProp>(
            System.Linq.Expressions.Expression<Func<T, TProp>> property) => this;

        public DocumentMapping<T> EncryptField<TProp>(
            System.Linq.Expressions.Expression<Func<T, TProp>> property,
            EncryptionAlgorithm algorithm = EncryptionAlgorithm.Aes256Gcm) => this;
    }
}

""";

    [Test]
    public async Task Where_over_encrypted_field_reports_ADB100()
    {
        var source = """
public sealed class Customer
{
    public long Id { get; set; }
    [AeroDB.Sable.Encrypt]
    public string SocialSecurityNumber { get; set; } = "";
}

public static class QueryCode
{
    public static IQueryable<Customer> Find(IQueryable<Customer> source) =>
        source.Where(customer => customer.{|#0:SocialSecurityNumber|} == "123-45-6789");
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult(EncryptedFieldQueryAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("SocialSecurityNumber", "Where"));
    }

    [Test]
    public async Task OrderBy_over_encrypted_field_reports_ADB100()
    {
        var source = """
public sealed class Customer
{
    [AeroDB.Sable.Encrypt]
    public string Secret { get; set; } = "";
}

public static class QueryCode
{
    public static IQueryable<Customer> Sort(IQueryable<Customer> source) =>
        source.OrderBy(customer => customer.{|#0:Secret|});
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult(EncryptedFieldQueryAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("Secret", "OrderBy"));
    }

    [Test]
    public async Task Projection_grouping_and_aggregate_over_encrypted_field_report_ADB100()
    {
        var source = """
public sealed class Customer
{
    [AeroDB.Sable.Encrypt]
    public string Secret { get; set; } = "";
}

public static class QueryCode
{
    public static IQueryable<string> Project(IQueryable<Customer> source) =>
        source.Select(customer => customer.{|#0:Secret|});

    public static IQueryable<IGrouping<string, Customer>> Group(IQueryable<Customer> source) =>
        source.GroupBy(customer => customer.{|#1:Secret|});

    public static int Count(IQueryable<Customer> source) =>
        source.Count(customer => customer.{|#2:Secret|} == "value");
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult(EncryptedFieldQueryAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("Secret", "Select"),
            new DiagnosticResult(EncryptedFieldQueryAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(1)
                .WithArguments("Secret", "GroupBy"),
            new DiagnosticResult(EncryptedFieldQueryAnalyzer.DiagnosticId, Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(2)
                .WithArguments("Secret", "Count"));
    }

    [Test]
    public async Task Clear_field_query_has_no_diagnostic()
    {
        var source = """
public sealed class Customer
{
    public long Id { get; set; }
    [AeroDB.Sable.Encrypt]
    public string Secret { get; set; } = "";
}

public static class QueryCode
{
    public static IQueryable<Customer> Find(IQueryable<Customer> source) =>
        source.Where(customer => customer.Id == 42);
}
""";

        await VerifyAsync(source);
    }

    [Test]
    public async Task Enumerable_query_after_materialization_has_no_diagnostic()
    {
        var source = """
public sealed class Customer
{
    [AeroDB.Sable.Encrypt]
    public string Secret { get; set; } = "";
}

public static class QueryCode
{
    public static IEnumerable<Customer> Find(IEnumerable<Customer> source) =>
        source.Where(customer => customer.Secret == "value");
}
""";

        await VerifyAsync(source);
    }

    [Test]
    public async Task Unsupported_encrypted_property_type_reports_ADB101()
    {
        var source = """
public sealed class Customer
{
    [AeroDB.Sable.Encrypt]
    public int {|#0:SecretNumber|} { get; set; }
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult("ADB101", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("SecretNumber", "int"));
    }

    [Test]
    public async Task Blind_index_without_encrypt_reports_ADB102()
    {
        var source = """
public sealed class Customer
{
    [AeroDB.Sable.BlindIndex]
    public string {|#0:SocialSecurityNumber|} { get; set; } = "";
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult("ADB102", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("SocialSecurityNumber"));
    }

    [Test]
    public async Task Blind_index_on_encrypted_string_has_no_mapping_diagnostic()
    {
        var source = """
public sealed class Customer
{
    [AeroDB.Sable.Encrypt]
    [AeroDB.Sable.BlindIndex]
    public string SocialSecurityNumber { get; set; } = "";
}
""";

        await VerifyAsync(source);
    }

    [Test]
    public async Task Undefined_blind_index_enum_reports_ADB106()
    {
        var source = """
public sealed class Customer
{
    [AeroDB.Sable.Encrypt]
    [AeroDB.Sable.BlindIndex(Normalizer = (AeroDB.Sable.BlindIndexNormalizer)999)]
    public string {|#0:SocialSecurityNumber|} { get; set; } = "";
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult("ADB106", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments(
                    "[BlindIndex]",
                    "Normalizer",
                    "SocialSecurityNumber",
                    "999"));
    }

    [Test]
    public async Task Undefined_encrypt_enum_reports_ADB106()
    {
        var source = """
public sealed class Customer
{
    [AeroDB.Sable.Encrypt((AeroDB.Sable.EncryptionAlgorithm)999)]
    public string {|#0:Secret|} { get; set; } = "";
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult("ADB106", Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments(
                    "[Encrypt]",
                    "Algorithm",
                    "Secret",
                    "999"));
    }

    [Test]
    public async Task Encrypt_attribute_on_Id_reports_ADB107()
    {
        var source = """
public sealed class Customer
{
    [AeroDB.Sable.Encrypt]
    public string {|#0:Id|} { get; set; } = "";
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult(
                    EncryptedFieldQueryAnalyzer.IdentityDiagnosticId,
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("Id"));
    }

    [Test]
    public async Task EncryptField_on_default_Id_reports_ADB107()
    {
        var source = """
public sealed class Customer
{
    public string Id { get; set; } = "";
}

public static class MappingCode
{
    public static void Configure(AeroDB.Sable.DocumentMapping<Customer> mapping) =>
        mapping.EncryptField(customer => customer.{|#0:Id|});
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult(
                    EncryptedFieldQueryAnalyzer.IdentityDiagnosticId,
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("Id"));
    }

    [Test]
    public async Task EncryptField_on_custom_identity_chain_reports_ADB107()
    {
        var source = """
public sealed class Customer
{
    public string ExternalKey { get; set; } = "";
}

public static class MappingCode
{
    public static void Configure(AeroDB.Sable.DocumentMapping<Customer> mapping) =>
        mapping
            .Identity(customer => customer.ExternalKey)
            .EncryptField(customer => customer.{|#0:ExternalKey|});
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult(
                    EncryptedFieldQueryAnalyzer.IdentityDiagnosticId,
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("ExternalKey"));
    }

    [Test]
    public async Task Identity_on_previously_encrypted_custom_key_chain_reports_ADB107()
    {
        var source = """
public sealed class Customer
{
    public string ExternalKey { get; set; } = "";
}

public static class MappingCode
{
    public static void Configure(AeroDB.Sable.DocumentMapping<Customer> mapping) =>
        mapping
            .EncryptField(customer => customer.{|#0:ExternalKey|})
            .Identity(customer => customer.ExternalKey);
}
""";

        await VerifyAsync(
            source,
            new DiagnosticResult(
                    EncryptedFieldQueryAnalyzer.IdentityDiagnosticId,
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("ExternalKey"));
    }
}
