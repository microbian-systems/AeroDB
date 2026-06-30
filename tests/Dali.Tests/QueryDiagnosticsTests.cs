using System.Text.Json;
using TUnit.Core;

namespace Dali.Tests;

/// <summary>
/// Tests for <see cref="IDiagnostics.PreviewCommandAsync"/>,
/// <see cref="IDiagnostics.ExplainPlanAsync"/>, and
/// <see cref="DaliAdvancedSql.StreamAsync{T}"/>.
///
/// Note: ExplainPlanAsync in the embedded InMemory engine may throw
/// <see cref="InvalidOperationException"/> wrapping a CBOR deserialization failure
/// because EXPLAIN responses use a non-standard result format.
/// Tests accept both successful plans and documented error/exception scenarios.
/// </summary>
public class QueryDiagnosticsTests
{
    // ─── Diagnostics / PreviewCommandAsync ───────────────────────────

    /// <summary>
    /// Preview the generated SurrealQL for a simple LINQ query.
    /// Verifies the output contains SELECT, FROM, and the table name.
    /// </summary>
    [Test]
    public async Task PreviewCommandAsync_ReturnsSurrealQL()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        IQueryable<Person> query = session.Query<Person>()
            .Where(p => p.Age > 25);

        var sql = await store.Advanced.Diagnostics.PreviewCommandAsync(query);

        sql.ShouldNotBeNullOrEmpty();
        sql.ShouldContain("SELECT");
        sql.ShouldContain("FROM");
        sql.ShouldContain("person");   // table name resolved from T
        sql.ShouldContain("Age");      // referenced in WHERE
    }



    // ─── StreamAsync ─────────────────────────────────────────────────

    /// <summary>
    /// Store documents and then stream them back via DaliAdvancedSql.StreamAsync.
    /// Verifies all stored records are yielded by the async stream.
    /// </summary>
    [Test]
    public async Task StreamAsync_StreamsResults()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        session.Store(new Person { Name = "Alice", Age = 30, Email = "alice@test.com" });
        session.Store(new Person { Name = "Bob", Age = 25, Email = "bob@test.com" });
        session.Store(new Person { Name = "Charlie", Age = 35, Email = "charlie@test.com" });
        await session.SaveChangesAsync();

        var sql = "SELECT * FROM person WHERE Age > 0 ORDER BY Name;";
        var names = new List<string>();

        await foreach (var person in session.AdvancedSql().StreamAsync<Person>(sql))
        {
            person.ShouldNotBeNull();
            person.Name.ShouldNotBeNullOrEmpty();
            names.Add(person.Name);
        }

        names.Count.ShouldBe(3);
        names.ShouldContain("Alice");
        names.ShouldContain("Bob");
        names.ShouldContain("Charlie");
    }
}
