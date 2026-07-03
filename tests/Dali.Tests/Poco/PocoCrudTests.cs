using TUnit.Core;
using Dali;

namespace Dali.Tests.Poco;

// ─── POCO Types (Plain Old CLR Objects with different identity types) ───

public class PocoLong
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

public class PocoInt
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

public class PocoString
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

public class PocoGuid
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

public class PocoUlong
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

public class PocoUint
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

// ─── CRUD Tests ────────────────────────────────────────────────────

public class PocoCrudTests
{
    // ─── long identity ─────────────────────────────────────────────

    [Test]
    public async Task PocoLong_CRUD()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoLong>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var entityId = 42L;

        // ─── Store ──────────────────────────────────────────
        var entity = new PocoLong { Id = entityId, Name = "Widget", Score = 100 };
        session.Store(entity);
        await session.SaveChangesAsync();

        // ─── Verify Store via Query ─────────────────────────
        var results = await session.Query<PocoLong>()
            .Where(x => x.Score == 100)
            .ToListAsync();
        results.Count.ShouldBeGreaterThan(0);
        results[0].Id.ShouldBe(entityId);
        results[0].Name.ShouldBe("Widget");
        results[0].Score.ShouldBe(100);

        // ─── Load by ID ────────────────────────────────────
        var loaded = await session.LoadAsync<PocoLong>(entityId.ToString());
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Widget");
        loaded.Score.ShouldBe(100);
        loaded.Id.ShouldBe(entityId);

        // ─── Update ─────────────────────────────────────────
        loaded!.Name = "Super Widget";
        loaded.Score = 200;
        session.Store(loaded);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<PocoLong>(entityId.ToString());
        reloaded.ShouldNotBeNull();
        reloaded.Name.ShouldBe("Super Widget");
        reloaded.Score.ShouldBe(200);

        // ─── Delete by entity ───────────────────────────────
        session.Delete(reloaded);
        await session.SaveChangesAsync();

        var afterDelete = await session.LoadAsync<PocoLong>(entityId.ToString());
        afterDelete.ShouldBeNull();

        // ─── Delete by string ID ────────────────────────────
        var entity2 = new PocoLong { Id = 99, Name = "Temp", Score = 1 };
        session.Store(entity2);
        await session.SaveChangesAsync();

        session.Delete<PocoLong>("99");
        await session.SaveChangesAsync();

        var afterStrDelete = await session.LoadAsync<PocoLong>(99.ToString());
        afterStrDelete.ShouldBeNull();

        // ─── Load nonexistent returns null ──────────────────
        var nonexistent = await session.LoadAsync<PocoLong>(999999.ToString());
        nonexistent.ShouldBeNull();
    }

    // ─── int identity ──────────────────────────────────────────────

    [Test]
    public async Task PocoInt_CRUD()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoInt>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var entityId = 42;

        // Store
        var entity = new PocoInt { Id = entityId, Name = "Widget", Score = 100 };
        session.Store(entity);
        await session.SaveChangesAsync();

        // Load by ID (use string overload to route through LoadPocoAsync)
        var loaded = await session.LoadAsync<PocoInt>(entityId.ToString());
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Widget");
        loaded.Score.ShouldBe(100);
        loaded.Id.ShouldBe(entityId);

        // Query with Where
        var results = await session.Query<PocoInt>()
            .Where(x => x.Score == 100)
            .ToListAsync();
        results.Count.ShouldBeGreaterThan(0);
        results[0].Id.ShouldBe(entityId);

        // Update
        loaded!.Name = "Super Widget";
        loaded.Score = 200;
        session.Store(loaded);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<PocoInt>(entityId.ToString());
        reloaded.ShouldNotBeNull();
        reloaded.Name.ShouldBe("Super Widget");
        reloaded.Score.ShouldBe(200);

        // Delete by entity
        session.Delete(reloaded);
        await session.SaveChangesAsync();

        var afterDelete = await session.LoadAsync<PocoInt>(entityId.ToString());
        afterDelete.ShouldBeNull();

        // Delete by string ID
        var entity2 = new PocoInt { Id = 99, Name = "Temp", Score = 1 };
        session.Store(entity2);
        await session.SaveChangesAsync();

        session.Delete<PocoInt>("99");
        await session.SaveChangesAsync();

        var afterStrDelete = await session.LoadAsync<PocoInt>(99.ToString());
        afterStrDelete.ShouldBeNull();

        // Load nonexistent returns null
        var nonexistent = await session.LoadAsync<PocoInt>(999999.ToString());
        nonexistent.ShouldBeNull();
    }

    // ─── string identity ───────────────────────────────────────────

    [Test]
    public async Task PocoString_CRUD()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoString>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var entityId = "abc-123";

        // Store
        var entity = new PocoString { Id = entityId, Name = "Widget", Score = 100 };
        session.Store(entity);
        await session.SaveChangesAsync();

        // Load by ID
        var loaded = await session.LoadAsync<PocoString>(entityId);
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Widget");
        loaded.Score.ShouldBe(100);
        loaded.Id.ShouldBe(entityId);

        // Query with Where
        var results = await session.Query<PocoString>()
            .Where(x => x.Score == 100)
            .ToListAsync();
        results.Count.ShouldBeGreaterThan(0);
        results[0].Id.ShouldBe(entityId);

        // Update
        loaded!.Name = "Super Widget";
        loaded.Score = 200;
        session.Store(loaded);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<PocoString>(entityId);
        reloaded.ShouldNotBeNull();
        reloaded.Name.ShouldBe("Super Widget");
        reloaded.Score.ShouldBe(200);

        // Delete by entity
        session.Delete(reloaded);
        await session.SaveChangesAsync();

        var afterDelete = await session.LoadAsync<PocoString>(entityId);
        afterDelete.ShouldBeNull();

        // Delete by string ID
        var entity2 = new PocoString { Id = "xyz-999", Name = "Temp", Score = 1 };
        session.Store(entity2);
        await session.SaveChangesAsync();

        session.Delete<PocoString>("xyz-999");
        await session.SaveChangesAsync();

        var afterStrDelete = await session.LoadAsync<PocoString>("xyz-999");
        afterStrDelete.ShouldBeNull();

        // Load nonexistent returns null
        var nonexistent = await session.LoadAsync<PocoString>("nonexistent");
        nonexistent.ShouldBeNull();
    }

    // ─── Guid identity ─────────────────────────────────────────────

    [Test]
    public async Task PocoGuid_CRUD()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoGuid>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var entityId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        // Store
        var entity = new PocoGuid { Id = entityId, Name = "Widget", Score = 100 };
        session.Store(entity);
        await session.SaveChangesAsync();

        // Load by ID (use string overload to route through LoadPocoAsync)
        var loaded = await session.LoadAsync<PocoGuid>(entityId.ToString());
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Widget");
        loaded.Score.ShouldBe(100);
        loaded.Id.ShouldBe(entityId);

        // Query with Where
        var results = await session.Query<PocoGuid>()
            .Where(x => x.Score == 100)
            .ToListAsync();
        results.Count.ShouldBeGreaterThan(0);
        results[0].Id.ShouldBe(entityId);

        // Update
        loaded!.Name = "Super Widget";
        loaded.Score = 200;
        session.Store(loaded);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<PocoGuid>(entityId.ToString());
        reloaded.ShouldNotBeNull();
        reloaded.Name.ShouldBe("Super Widget");
        reloaded.Score.ShouldBe(200);

        // Delete by entity
        session.Delete(reloaded);
        await session.SaveChangesAsync();

        var afterDelete = await session.LoadAsync<PocoGuid>(entityId.ToString());
        afterDelete.ShouldBeNull();

        // Delete by string ID
        var entity2Id = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var entity2 = new PocoGuid { Id = entity2Id, Name = "Temp", Score = 1 };
        session.Store(entity2);
        await session.SaveChangesAsync();

        session.Delete<PocoGuid>(entity2Id.ToString());
        await session.SaveChangesAsync();

        var afterStrDelete = await session.LoadAsync<PocoGuid>(entity2Id.ToString());
        afterStrDelete.ShouldBeNull();

        // Load nonexistent returns null
        var nonexistent = await session.LoadAsync<PocoGuid>(Guid.NewGuid().ToString());
        nonexistent.ShouldBeNull();
    }

    // ─── ulong identity ────────────────────────────────────────────

    [Test]
    public async Task PocoUlong_CRUD()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoUlong>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var entityId = 42UL;

        // Store
        var entity = new PocoUlong { Id = entityId, Name = "Widget", Score = 100 };
        session.Store(entity);
        await session.SaveChangesAsync();

        // Load by ID (use string overload to route through LoadPocoAsync)
        var loaded = await session.LoadAsync<PocoUlong>(entityId.ToString());
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Widget");
        loaded.Score.ShouldBe(100);
        loaded.Id.ShouldBe(entityId);

        // Query with Where
        var results = await session.Query<PocoUlong>()
            .Where(x => x.Score == 100)
            .ToListAsync();
        results.Count.ShouldBeGreaterThan(0);
        results[0].Id.ShouldBe(entityId);

        // Update
        loaded!.Name = "Super Widget";
        loaded.Score = 200;
        session.Store(loaded);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<PocoUlong>(entityId.ToString());
        reloaded.ShouldNotBeNull();
        reloaded.Name.ShouldBe("Super Widget");
        reloaded.Score.ShouldBe(200);

        // Delete by entity
        session.Delete(reloaded);
        await session.SaveChangesAsync();

        var afterDelete = await session.LoadAsync<PocoUlong>(entityId.ToString());
        afterDelete.ShouldBeNull();

        // Delete by string ID
        var entity2 = new PocoUlong { Id = 99UL, Name = "Temp", Score = 1 };
        session.Store(entity2);
        await session.SaveChangesAsync();

        session.Delete<PocoUlong>("99");
        await session.SaveChangesAsync();

        var afterStrDelete = await session.LoadAsync<PocoUlong>(99UL.ToString());
        afterStrDelete.ShouldBeNull();

        // Load nonexistent returns null
        var nonexistent = await session.LoadAsync<PocoUlong>(999999UL.ToString());
        nonexistent.ShouldBeNull();
    }

    // ─── uint identity ─────────────────────────────────────────────

    [Test]
    public async Task PocoUint_CRUD()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoUint>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var entityId = 42U;

        // Store
        var entity = new PocoUint { Id = entityId, Name = "Widget", Score = 100 };
        session.Store(entity);
        await session.SaveChangesAsync();

        // Load by ID (use string overload to route through LoadPocoAsync)
        var loaded = await session.LoadAsync<PocoUint>(entityId.ToString());
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Widget");
        loaded.Score.ShouldBe(100);
        loaded.Id.ShouldBe(entityId);

        // Query with Where
        var results = await session.Query<PocoUint>()
            .Where(x => x.Score == 100)
            .ToListAsync();
        results.Count.ShouldBeGreaterThan(0);
        results[0].Id.ShouldBe(entityId);

        // Update
        loaded!.Name = "Super Widget";
        loaded.Score = 200;
        session.Store(loaded);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<PocoUint>(entityId.ToString());
        reloaded.ShouldNotBeNull();
        reloaded.Name.ShouldBe("Super Widget");
        reloaded.Score.ShouldBe(200);

        // Delete by entity
        session.Delete(reloaded);
        await session.SaveChangesAsync();

        var afterDelete = await session.LoadAsync<PocoUint>(entityId.ToString());
        afterDelete.ShouldBeNull();

        // Delete by string ID
        var entity2 = new PocoUint { Id = 99U, Name = "Temp", Score = 1 };
        session.Store(entity2);
        await session.SaveChangesAsync();

        session.Delete<PocoUint>("99");
        await session.SaveChangesAsync();

        var afterStrDelete = await session.LoadAsync<PocoUint>(99U.ToString());
        afterStrDelete.ShouldBeNull();

        // Load nonexistent returns null
        var nonexistent = await session.LoadAsync<PocoUint>(999999U.ToString());
        nonexistent.ShouldBeNull();
    }
}
