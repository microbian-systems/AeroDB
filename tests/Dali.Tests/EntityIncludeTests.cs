using TUnit.Core;

namespace Dali.Tests;

public class EntityIncludeTests
{
    [Test]
    public async Task IncludeReverse_loads_children_for_Entity_types()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var parent = new EntityIncludeParent { Title = "Parent Title" };
        session.Store(parent);

        var child1 = new EntityIncludeChild { Content = "Child One", ParentId = parent.Id };
        var child2 = new EntityIncludeChild { Content = "Child Two", ParentId = parent.Id };
        session.Store(child1);
        session.Store(child2);

        await session.SaveChangesAsync();

        var results = await session.Query<EntityIncludeParent>()
            .IncludeReverse(p => p.Children, "ParentId")
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("Parent Title");
        results[0].Children.ShouldNotBeNull();
        results[0].Children.Count.ShouldBe(2);
        results[0].Children.ShouldContain(c => c.Content == "Child One");
        results[0].Children.ShouldContain(c => c.Content == "Child Two");
    }

    [Test]
    public async Task IncludeReverse_Entity_empty_children_returns_empty_collection()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var parent = new EntityIncludeParent { Title = "No Kids" };
        session.Store(parent);
        await session.SaveChangesAsync();

        var results = await session.Query<EntityIncludeParent>()
            .IncludeReverse(p => p.Children, "ParentId")
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Children.ShouldNotBeNull();
        results[0].Children.ShouldBeEmpty();
    }

    [Test]
    public async Task IncludeReverse_Entity_filter_include_filters_parents()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var parent1 = new EntityIncludeParent { Title = "Has Kids" };
        var parent2 = new EntityIncludeParent { Title = "No Kids" };
        session.Store(parent1);
        session.Store(parent2);

        var child = new EntityIncludeChild { Content = "Only Child", ParentId = parent1.Id };
        session.Store(child);

        await session.SaveChangesAsync();

        // FilterInclude with .Any() should only keep parents with children
        var results = await session.Query<EntityIncludeParent>()
            .IncludeReverse(p => p.Children, "ParentId")
            .FilterInclude(p => p.Children, c => c.Any())
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("Has Kids");
        results[0].Children.Count.ShouldBe(1);
    }

    // ── String FK ──────────────────────────────────────────────

    [Test]
    public async Task IncludeReverse_Entity_stringFK_loads_children()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var parent = new EntityIncludeParentStr { Title = "String Parent", Id = "p-str-1" };
        session.Store(parent);
        session.Store(new EntityIncludeChildStr { Name = "S1", ParentId = "p-str-1" });
        session.Store(new EntityIncludeChildStr { Name = "S2", ParentId = "p-str-1" });
        await session.SaveChangesAsync();

        var results = await session.Query<EntityIncludeParentStr>()
            .IncludeReverse(p => p.Items, "ParentId")
            .ToListAsync();

        results[0].Items.Count.ShouldBe(2);
    }

    // ── Int FK ─────────────────────────────────────────────────

    [Test]
    public async Task IncludeReverse_Entity_intFK_loads_children()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var parent = new EntityIncludeParentInt { Title = "Int Parent", Id = 100 };
        session.Store(parent);
        session.Store(new EntityIncludeChildInt { Name = "I1", ParentId = 100 });
        session.Store(new EntityIncludeChildInt { Name = "I2", ParentId = 100 });
        await session.SaveChangesAsync();

        var results = await session.Query<EntityIncludeParentInt>()
            .IncludeReverse(p => p.Items, "ParentId")
            .ToListAsync();

        results[0].Items.Count.ShouldBe(2);
    }

    // ── Guid FK ────────────────────────────────────────────────

    [Test]
    public async Task IncludeReverse_Entity_guidFK_loads_children()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var parentId = Guid.NewGuid();
        var parent = new EntityIncludeParentGuid { Title = "Guid Parent", Id = parentId };
        session.Store(parent);
        session.Store(new EntityIncludeChildGuid { Name = "G1", ParentId = parentId });
        session.Store(new EntityIncludeChildGuid { Name = "G2", ParentId = parentId });
        await session.SaveChangesAsync();

        var results = await session.Query<EntityIncludeParentGuid>()
            .IncludeReverse(p => p.Items, "ParentId")
            .ToListAsync();

        results[0].Items.Count.ShouldBe(2);
    }

    // ── FirstOrDefaultAsync ────────────────────────────────────

    [Test]
    public async Task IncludeReverse_Entity_FirstOrDefaultAsync_loads_children()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var parent = new EntityIncludeParent { Title = "FirstOrDef Parent" };
        session.Store(parent);
        session.Store(new EntityIncludeChild { Content = "FOD1", ParentId = parent.Id });
        session.Store(new EntityIncludeChild { Content = "FOD2", ParentId = parent.Id });
        await session.SaveChangesAsync();

        var result = await session.Query<EntityIncludeParent>()
            .Where(p => p.Title == "FirstOrDef Parent")
            .IncludeReverse(p => p.Children, "ParentId")
            .FirstOrDefaultAsync();

        result.ShouldNotBeNull();
        result.Children.Count.ShouldBe(2);
    }

    // ── SingleOrDefaultAsync ───────────────────────────────────

    [Test]
    public async Task IncludeReverse_Entity_SingleOrDefaultAsync_loads_children()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var parent = new EntityIncludeParent { Title = "Single Parent" };
        session.Store(parent);
        session.Store(new EntityIncludeChild { Content = "SOD1", ParentId = parent.Id });
        session.Store(new EntityIncludeChild { Content = "SOD2", ParentId = parent.Id });
        await session.SaveChangesAsync();

        var result = await session.Query<EntityIncludeParent>()
            .Where(p => p.Title == "Single Parent")
            .IncludeReverse(p => p.Children, "ParentId")
            .SingleOrDefaultAsync();

        result.ShouldNotBeNull();
        result.Children.Count.ShouldBe(2);
    }
}
