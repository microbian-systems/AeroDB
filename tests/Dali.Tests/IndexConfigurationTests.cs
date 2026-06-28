using System.Linq.Expressions;
using System.Reflection;
using SurrealDb.Net.Models;

namespace Dali.Tests;

/// <summary>
/// Lightweight test model with FirstName/LastName for composite index tests,
/// plus value-type properties (Age, Quantity) for value-type index tests.
/// </summary>
public class PersonWithNames : Record
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public int Age { get; set; }
    public int Quantity { get; set; }
}

/// <summary>
/// Comprehensive unit tests for <see cref="DocumentMapping{T}.Index()"/> and its overloads.
/// No database required — all tests exercise the fluent schema API in-memory.
/// </summary>
public class IndexConfigurationTests
{
    // ──────────────────────────────────────────────
    //  1. Single-property Index() — basic
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_single_property_creates_index()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<Person>().Index(x => x.Name);

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Columns.ShouldBe(["Name"]);
        idx.Name.ShouldBe("idx_person_name");
        idx.IsUnique.ShouldBeFalse();
        idx.Type.ShouldBe(IndexType.Standard);
    }

    // ──────────────────────────────────────────────
    //  2. Single-property Index() with IsUnique
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_single_property_with_IsUnique_uses_uidx_prefix()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<Person>().Index(x => x.Name, c => c.IsUnique());

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Columns.ShouldBe(["Name"]);
        idx.Name.ShouldBe("uidx_person_name");
        idx.IsUnique.ShouldBeTrue();
    }

    // ──────────────────────────────────────────────
    //  3. Single-property Index() with WithName
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_single_property_with_WithName_respects_custom_name()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<Person>().Index(x => x.Name, c => c.WithName("my_custom_idx"));

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Name.ShouldBe("my_custom_idx");
        idx.Columns.ShouldBe(["Name"]);
        idx.IsUnique.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────
    //  4. Single-property Index() with WithName AND IsUnique
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_with_WithName_and_IsUnique_keeps_custom_name()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<Person>().Index(x => x.Name, c =>
        {
            c.WithName("my_custom_idx");
            c.IsUnique();
        });

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Name.ShouldBe("my_custom_idx");
        idx.Columns.ShouldBe(["Name"]);
        idx.IsUnique.ShouldBeTrue();
    }

    // ──────────────────────────────────────────────
    //  5. Multi-column Index() with anonymous type
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_anonymous_type_creates_composite_index()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<PersonWithNames>().Index(x => new { x.FirstName, x.LastName });

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Columns.ShouldBe(["FirstName", "LastName"]);
        idx.Name.ShouldBe("idx_person_with_names_first_name_last_name");
        idx.IsUnique.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────
    //  6. Multi-column Index() with anonymous type + IsUnique
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_anonymous_type_with_IsUnique_uses_uidx_prefix()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<PersonWithNames>().Index(
            x => new { x.FirstName, x.LastName },
            c => c.IsUnique());

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Columns.ShouldBe(["FirstName", "LastName"]);
        idx.Name.ShouldBe("uidx_person_with_names_first_name_last_name");
        idx.IsUnique.ShouldBeTrue();
    }

    // ──────────────────────────────────────────────
    //  7. Multi-column Index() with anonymous type + WithName
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_anonymous_type_with_WithName_respects_custom_name()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<PersonWithNames>().Index(
            x => new { x.FirstName, x.LastName },
            c => c.WithName("my_composite_idx"));

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Name.ShouldBe("my_composite_idx");
        idx.Columns.ShouldBe(["FirstName", "LastName"]);
        idx.IsUnique.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────
    //  8. Index() with value-type property (int)
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_value_type_property_handles_conversion()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        // Age is int — generic overload wraps the body with Expression.Convert(body, typeof(object))
        // producing a UnaryExpression(Convert) that must be handled by ExtractMemberFromBody
        var mapping = options.Schema.For<Person>().Index(x => x.Age);

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Columns.ShouldBe(["Age"]);
        idx.Name.ShouldBe("idx_person_age");
        idx.IsUnique.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────
    //  9. Index() with anonymous type containing value-type properties
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_anonymous_type_with_value_types()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        // Both Age and Quantity are int — anonymous type constructor uses boxed values
        var mapping = options.Schema.For<PersonWithNames>().Index(x => new { x.Age, x.Quantity });

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Columns.ShouldBe(["Age", "Quantity"]);
        idx.Name.ShouldBe("idx_person_with_names_age_quantity");
        idx.IsUnique.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────
    //  10. Index() with non-anonymous NewExpression throws
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_non_anonymous_new_expression_throws()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<Person>();

        // MemberInitExpression (new Person { Name = ... }) is not an anonymous type,
        // so it should fall through to ExtractMemberFromBody and throw
        Should.Throw<ArgumentException>(() =>
            mapping.Index(x => new Person { Name = x.Name }));
    }

    // ──────────────────────────────────────────────
    //  11. Generic Index<TProp>() delegates correctly
    // ──────────────────────────────────────────────

    [Test]
    public async Task Index_generic_overload_produces_same_result()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<Person>();

        // Compiler infers TProp = string → calls generic Index<TProp>()
        mapping.Index(x => x.Name);

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Columns.ShouldBe(["Name"]);
        idx.Name.ShouldBe("idx_person_name");
        idx.IsUnique.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────
    //  12. UniqueIndex() delegates correctly
    // ──────────────────────────────────────────────

    [Test]
    public async Task UniqueIndex_uses_uidx_prefix()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
        var mapping = options.Schema.For<Person>().UniqueIndex(x => x.Name);

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Columns.ShouldBe(["Name"]);
        idx.Name.ShouldBe("uidx_person_name");
        idx.IsUnique.ShouldBeTrue();
    }

    // ──────────────────────────────────────────────
    //  13. CompositeIndex() [Obsolete] still works
    // ──────────────────────────────────────────────

    [Test]
    public async Task CompositeIndex_creates_multi_column_index()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
#pragma warning disable CS0618 // Obsolete — testing that it still works
        var mapping = options.Schema.For<Person>().CompositeIndex(x => x.Name, x => x.Email);
#pragma warning restore CS0618

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Columns.ShouldBe(["Name", "Email"]);
        idx.Name.ShouldBe("idx_person_name_email");
        idx.IsUnique.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────
    //  14. UniqueCompositeIndex() [Obsolete] still works
    // ──────────────────────────────────────────────

    [Test]
    public async Task UniqueCompositeIndex_creates_unique_multi_column_index()
    {
        var options = new StoreOptions { Namespace = "test", Database = "test" };
#pragma warning disable CS0618 // Obsolete — testing that it still works
        var mapping = options.Schema.For<Person>().UniqueCompositeIndex(x => x.Name, x => x.Email);
#pragma warning restore CS0618

        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Columns.ShouldBe(["Name", "Email"]);
        idx.Name.ShouldBe("uidx_person_name_email");
        idx.IsUnique.ShouldBeTrue();
    }

    // ──────────────────────────────────────────────
    //  15. Both Index overloads produce identical results for single property
    // ──────────────────────────────────────────────

    [Test]
    public async Task Both_Index_overloads_produce_identical_index_for_single_property()
    {
        // Generic overload (TProp is inferred)
        var options1 = new StoreOptions { Namespace = "test", Database = "test" };
        options1.Schema.For<Person>().Index(x => x.Name);

        // Non-generic overload via explicit Expression<Func<Person, object>>
        var options2 = new StoreOptions { Namespace = "test", Database = "test" };
        options2.Schema.For<Person>().Index((Expression<Func<Person, object>>)(x => x.Name));

        var idx1 = options1.Schema.Mappings[typeof(Person)].Indices[0];
        var idx2 = options2.Schema.Mappings[typeof(Person)].Indices[0];

        idx1.Columns.ShouldBe(idx2.Columns);
        idx1.Name.ShouldBe(idx2.Name);
        idx1.IsUnique.ShouldBe(idx2.IsUnique);
        idx1.Type.ShouldBe(idx2.Type);
    }

    // ──────────────────────────────────────────────
    //  16. SURQL generation: verify actual SQL output for standard index
    // ──────────────────────────────────────────────

    [Test]
    public async Task BuildStandardIndex_produces_correct_surql_for_composite()
    {
        var idx = new IndexDefinition
        {
            Name = "idx_person_name_email",
            Columns = ["Name", "Email"],
            Type = IndexType.Standard,
            IsUnique = false
        };

        var surql = BuildBuilderMethod("BuildStandardIndex", idx, "person");
        surql.ShouldBe("DEFINE INDEX idx_person_name_email ON TABLE person COLUMNS Name, Email;");
    }

    [Test]
    public async Task BuildStandardIndex_with_Unique_includes_UNIQUE_keyword()
    {
        var idx = new IndexDefinition
        {
            Name = "uidx_person_email",
            Columns = ["Email"],
            Type = IndexType.Standard,
            IsUnique = true
        };

        var surql = BuildBuilderMethod("BuildStandardIndex", idx, "person");
        surql.ShouldBe("DEFINE INDEX uidx_person_email ON TABLE person COLUMNS Email UNIQUE;");
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Invokes a private static SurrealQL builder method on <see cref="SchemaManager"/> for unit testing.
    /// </summary>
    private static string BuildBuilderMethod(string methodName, IndexDefinition index, string tableName)
    {
        var method = typeof(SchemaManager).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null) throw new InvalidOperationException($"Method {methodName} not found on SchemaManager");
        return (string)method.Invoke(null, [tableName, index])!;
    }
}
