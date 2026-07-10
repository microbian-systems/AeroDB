using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Tests for <see cref="QueryStatistics"/> and the <see cref="StatsExtensions.Stats{T}"/> extension.
/// </summary>
public class StatsTests
{
    // ─── Helpers ─────────────────────────────────────────────────────

    private static async Task SeedPeople(IDocumentSession session, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            session.Store(new Person
            {
                Name = $"Person{i}",
                Age = 20 + i,
                Email = $"person{i}@test.com"
            });
        }
        await session.SaveChangesAsync();
    }

    private static async Task SeedAges(IDocumentSession session, params int[] ages)
    {
        for (var i = 0; i < ages.Length; i++)
        {
            session.Store(new Person
            {
                Name = $"Person{i + 1}",
                Age = ages[i],
                Email = $"person{i + 1}@test.com"
            });
        }
        await session.SaveChangesAsync();
    }

    // ─── Tests ───────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that Stats returns the total row count when no filters are applied.
    /// TotalResults should equal the full table count, same as results.Count.
    /// </summary>
    [Test]
    public async Task Stats_returns_total_count_when_no_filters()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 5);

        var results = await session.Query<Person>()
            .Stats(out var stats)
            .ToListAsync();

        results.Count.ShouldBe(5);
        stats.TotalResults.ShouldBe(5);
    }

    /// <summary>
    /// Verifies that Stats returns the count of items matching the filter,
    /// not the total table count.
    /// </summary>
    [Test]
    public async Task Stats_returns_filtered_count()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Ages: 21, 25, 26, 30, 35 → 3 over 25 (26, 30, 35)
        await SeedAges(session, 21, 25, 26, 30, 35);

        var results = await session.Query<Person>()
            .Where(p => p.Age > 25)
            .Stats(out var stats)
            .ToListAsync();

        results.Count.ShouldBe(3);
        stats.TotalResults.ShouldBe(3);
    }

    /// <summary>
    /// Verifies that Stats returns the total unfiltered count even when
    /// Skip and Take are applied for paging.
    /// </summary>
    [Test]
    public async Task Stats_with_Skip_Take_returns_correct_count()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 10);

        var results = await session.Query<Person>()
            .Stats(out var stats)
            .Skip(3)
            .Take(2)
            .ToListAsync();

        results.Count.ShouldBe(2);        // page size
        stats.TotalResults.ShouldBe(10);  // total, not page count
    }

    /// <summary>
    /// Verifies that Stats returns correct total count when combined
    /// with ToListAsync and a filter.
    /// </summary>
    [Test]
    public async Task Stats_with_empty_result()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var results = await session.Query<Person>()
            .Stats(out var stats)
            .ToListAsync();

        results.Count.ShouldBe(0);
        stats.TotalResults.ShouldBe(0);
    }

    /// <summary>
    /// Verifies that the <c>out QueryStatistics stats</c> syntax works and
    /// the same object reference is populated after query execution.
    /// </summary>
    [Test]
    public async Task Stats_out_parameter_populated()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 4);

        QueryStatistics captured;
        var q = session.Query<Person>().Stats(out captured);
        captured.TotalResults.ShouldBe(0); // not yet populated

        var results = await q.ToListAsync();

        results.Count.ShouldBe(4);
        captured.TotalResults.ShouldBe(4);
    }

    /// <summary>
    /// Verifies that Stats works correctly after a Where chain.
    /// </summary>
    [Test]
    public async Task Stats_after_Where_chain()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        session.Store(new Person { Name = "Charlie", Age = 35 });
        session.Store(new Person { Name = "Diana", Age = 22 });
        await session.SaveChangesAsync();

        var results = await session.Query<Person>()
            .Where(p => p.Name == "Alice")
            .Stats(out var stats)
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
        stats.TotalResults.ShouldBe(1);
    }

    /// <summary>
    /// Verifies that Stats works correctly when OrderBy is in the chain.
    /// </summary>
    [Test]
    public async Task Stats_with_OrderBy()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedAges(session, 30, 25, 35, 22, 28);

        var results = await session.Query<Person>()
            .Stats(out var stats)
            .OrderBy(p => p.Age)
            .ToListAsync();

        results.Count.ShouldBe(5);
        stats.TotalResults.ShouldBe(5);
        results[0].Age.ShouldBe(22);
        results[^1].Age.ShouldBe(35);
    }
}
