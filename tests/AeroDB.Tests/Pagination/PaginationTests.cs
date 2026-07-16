using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests.Pagination;

/// <summary>
/// Tests for <see cref="PagedListQueryableExtensions.ToPagedListAsync{T}"/>
/// on <see cref="ISurrealDbQueryable{T}"/>.
/// </summary>
public class PaginationTests
{
    // ─── Helpers ─────────────────────────────────────────────────────

    private static async Task SeedPeople(IDocumentSession session, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            session.Store(new Person
            {
                Name = $"Person{i}",
                Age = 20 + (i % 10),
                Email = $"person{i}@test.com"
            });
        }
        await session.SaveChangesAsync();
    }

    // ─── Tests ───────────────────────────────────────────────────────

    /// <summary>
    /// First page of a multi-page result should have the correct page metadata.
    /// </summary>
    [Test]
    public async Task ToPagedList_first_page()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 25);

        var paged = await session.Query<Person>().ToPagedListAsync(1, 10);

        paged.PageNumber.ShouldBe(1);
        paged.PageSize.ShouldBe(10);
        paged.TotalItemCount.ShouldBe(25);
        paged.PageCount.ShouldBe(3);
        paged.Count.ShouldBe(10);
        paged.HasNextPage.ShouldBeTrue();
        paged.HasPreviousPage.ShouldBeFalse();
        paged.IsFirstPage.ShouldBeTrue();
        paged.IsLastPage.ShouldBeFalse();
    }

    /// <summary>
    /// Second page should have HasPreviousPage and HasNextPage both true
    /// when there are three pages.
    /// </summary>
    [Test]
    public async Task ToPagedList_second_page()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 25);

        var paged = await session.Query<Person>().ToPagedListAsync(2, 10);

        paged.PageNumber.ShouldBe(2);
        paged.PageSize.ShouldBe(10);
        paged.TotalItemCount.ShouldBe(25);
        paged.PageCount.ShouldBe(3);
        paged.Count.ShouldBe(10);
        paged.HasPreviousPage.ShouldBeTrue();
        paged.HasNextPage.ShouldBeTrue();
    }

    /// <summary>
    /// Last partial page should have fewer items, IsLastPage=true,
    /// and HasNextPage=false.
    /// </summary>
    [Test]
    public async Task ToPagedList_last_partial_page()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 25);

        var paged = await session.Query<Person>().ToPagedListAsync(3, 10);

        paged.PageNumber.ShouldBe(3);
        paged.Count.ShouldBe(5);
        paged.TotalItemCount.ShouldBe(25);
        paged.PageCount.ShouldBe(3);
        paged.IsLastPage.ShouldBeTrue();
        paged.HasNextPage.ShouldBeFalse();
    }

    /// <summary>
    /// When data fits on a single page, both IsFirstPage and IsLastPage
    /// should be true.
    /// </summary>
    [Test]
    public async Task ToPagedList_single_page()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 3);

        var paged = await session.Query<Person>().ToPagedListAsync(1, 10);

        paged.PageNumber.ShouldBe(1);
        paged.PageSize.ShouldBe(10);
        paged.TotalItemCount.ShouldBe(3);
        paged.PageCount.ShouldBe(1);
        paged.Count.ShouldBe(3);
        paged.IsFirstPage.ShouldBeTrue();
        paged.IsLastPage.ShouldBeTrue();
        paged.HasNextPage.ShouldBeFalse();
        paged.HasPreviousPage.ShouldBeFalse();
    }

    /// <summary>
    /// Empty table should return a paged list with zero counts.
    /// </summary>
    [Test]
    public async Task ToPagedList_empty()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var paged = await session.Query<Person>().ToPagedListAsync(1, 10);

        paged.PageNumber.ShouldBe(1);
        paged.PageSize.ShouldBe(10);
        paged.Count.ShouldBe(0);
        paged.TotalItemCount.ShouldBe(0);
        paged.PageCount.ShouldBe(0);
    }

    /// <summary>
    /// Page size of 1 with 5 items should give PageCount=5 and each page
    /// has one item. Page 3 should be the third item.
    /// </summary>
    [Test]
    public async Task ToPagedList_page_size_1()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 5);

        var paged = await session.Query<Person>().ToPagedListAsync(3, 1);

        paged.PageNumber.ShouldBe(3);
        paged.PageSize.ShouldBe(1);
        paged.Count.ShouldBe(1);
        paged.TotalItemCount.ShouldBe(5);
        paged.PageCount.ShouldBe(5);
        paged.HasPreviousPage.ShouldBeTrue();
        paged.HasNextPage.ShouldBeTrue();
        paged.IsFirstPage.ShouldBeFalse();
        paged.IsLastPage.ShouldBeFalse();
        paged.FirstItemOnPage.ShouldBe(3);
        paged.LastItemOnPage.ShouldBe(3);
    }

    /// <summary>
    /// When a Where filter is applied, TotalItemCount should reflect only
    /// the filtered count, not the full table.
    /// </summary>
    [Test]
    public async Task ToPagedList_with_where_filter()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Seed 10 persons: ages 21..30 → 5 over age 25 (26..30)
        for (var i = 1; i <= 10; i++)
        {
            session.Store(new Person
            {
                Name = $"Person{i}",
                Age = 20 + i,
                Email = $"person{i}@test.com"
            });
        }
        await session.SaveChangesAsync();

        var paged = await session.Query<Person>()
            .Where(p => p.Age > 25)
            .ToPagedListAsync(1, 3);

        paged.PageNumber.ShouldBe(1);
        paged.PageSize.ShouldBe(3);
        paged.Count.ShouldBe(3);
        paged.TotalItemCount.ShouldBe(5);
        paged.PageCount.ShouldBe(2);
        paged.HasNextPage.ShouldBeTrue();
        paged.IsFirstPage.ShouldBeTrue();
    }

    /// <summary>
    /// Using useCountQuery=true should produce the same results as the
    /// default Stats-based approach.
    /// </summary>
    [Test]
    public async Task ToPagedList_use_count_query()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 25);

        var paged = await AeroDB.Sable.Pagination.PagedListQueryableExtensions.ToPagedListAsync(session.Query<Person>(), 2, 10, useCountQuery: true);

        paged.PageNumber.ShouldBe(2);
        paged.PageSize.ShouldBe(10);
        paged.TotalItemCount.ShouldBe(25);
        paged.PageCount.ShouldBe(3);
        paged.Count.ShouldBe(10);
        paged.HasPreviousPage.ShouldBeTrue();
        paged.HasNextPage.ShouldBeTrue();
        paged.IsFirstPage.ShouldBeFalse();
        paged.IsLastPage.ShouldBeFalse();
    }

    [Test]
    public async Task ToPagedList_from_IQueryable_does_not_redispatch_recursively()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 3);
        IQueryable<Person> query = session.Query<Person>();

        var paged = await AeroDB.Sable.Pagination.PagedListQueryableExtensions
            .ToPagedListAsync(query, 1, 2);

        paged.Count.ShouldBe(2);
        paged.TotalItemCount.ShouldBe(3);
    }

    /// <summary>
    /// Page number of 0 should throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    [Test]
    public async Task ToPagedList_page_number_zero_throws()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 5);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await session.Query<Person>().ToPagedListAsync(0, 10));
    }

    /// <summary>
    /// Page size of 0 should throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    [Test]
    public async Task ToPagedList_page_size_zero_throws()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 5);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await session.Query<Person>().ToPagedListAsync(1, 0));
    }

    /// <summary>
    /// Verifies FirstItemOnPage and LastItemOnPage for a full middle page.
    /// Page 2 of 15 items, page size 5 → first=6, last=10.
    /// </summary>
    [Test]
    public async Task ToPagedList_FirstItemOnPage_LastItemOnPage()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 15);

        var paged = await session.Query<Person>().ToPagedListAsync(2, 5);

        paged.FirstItemOnPage.ShouldBe(6);
        paged.LastItemOnPage.ShouldBe(10);
        paged.Count.ShouldBe(5);
        paged.TotalItemCount.ShouldBe(15);
    }

    /// <summary>
    /// Verifies FirstItemOnPage and LastItemOnPage for a partial last page.
    /// Page 3 of 15 items, page size 5 (only 5 left) → first=11, last=15.
    /// </summary>
    [Test]
    public async Task ToPagedList_FirstItemOnPage_last_partial()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await SeedPeople(session, 15);

        var paged = await session.Query<Person>().ToPagedListAsync(3, 5);

        paged.FirstItemOnPage.ShouldBe(11);
        paged.LastItemOnPage.ShouldBe(15);
        paged.Count.ShouldBe(5);
        paged.TotalItemCount.ShouldBe(15);
    }
}
