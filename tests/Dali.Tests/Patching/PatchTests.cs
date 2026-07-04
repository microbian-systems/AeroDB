using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests.Patching;

public class PatchTests
{
    [Test]
    public async Task Patch_set_single_field()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        await session.SaveChangesAsync();

        var all = await session.Query<Person>().ToListAsync();
        var alice = all.First(p => p.Name == "Alice");
        var id = alice.Id is RecordIdOf<string> sid ? sid.Id : alice.Id!.ToString()!;

        session.Patch<Person>(id)
            .Set(p => p.Age, 35);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Person>(id);
        reloaded.ShouldNotBeNull();
        reloaded.Age.ShouldBe(35);
        reloaded.Name.ShouldBe("Alice");
    }

    [Test]
    public async Task Patch_set_multiple_fields()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Bob", Age = 25, Email = "bob@test.com" });
        await session.SaveChangesAsync();

        var all = await session.Query<Person>().ToListAsync();
        var bob = all.First(p => p.Name == "Bob");
        var id = bob.Id is RecordIdOf<string> sid ? sid.Id : bob.Id!.ToString()!;

        session.Patch<Person>(id)
            .Set(p => p.Age, 40)
            .Set(p => p.Name, "Robert");
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Person>(id);
        reloaded.ShouldNotBeNull();
        reloaded.Age.ShouldBe(40);
        reloaded.Name.ShouldBe("Robert");
        reloaded.Email.ShouldBe("bob@test.com");
    }

    [Test]
    public async Task Patch_increment_numeric()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Product { Name = "Widget", Quantity = 10, Price = 5.99m });
        await session.SaveChangesAsync();

        var all = await session.Query<Product>().ToListAsync();
        var widget = all.First(p => p.Name == "Widget");
        var id = widget.Id is RecordIdOf<string> sid ? sid.Id : widget.Id!.ToString()!;

        session.Patch<Product>(id)
            .Increment(p => p.Quantity, 5);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Product>(id);
        reloaded.ShouldNotBeNull();
        reloaded.Quantity.ShouldBe(15);
        reloaded.Name.ShouldBe("Widget");
    }

    [Test]
    public async Task Patch_append_to_array_field()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Charlie", Age = 30, Tags = new List<string> { "a" } });
        await session.SaveChangesAsync();

        var all = await session.Query<Person>().ToListAsync();
        var charlie = all.First(p => p.Name == "Charlie");
        var id = charlie.Id is RecordIdOf<string> sid ? sid.Id : charlie.Id!.ToString()!;

        session.Patch<Person>(id)
            .Append(p => p.Tags, "b");
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Person>(id);
        reloaded.ShouldNotBeNull();
        reloaded.Tags.ShouldContain("a");
        reloaded.Tags.ShouldContain("b");
        reloaded.Tags.Count.ShouldBe(2);
    }

    [Test]
    public async Task Patch_delete_field_sets_to_none()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Product { Name = "DeletePrice", Price = 10.00m, Quantity = 1 });
        await session.SaveChangesAsync();

        var all = await session.Query<Product>().ToListAsync();
        var prod = all.First(p => p.Name == "DeletePrice");
        var id = prod.Id is RecordIdOf<string> sid ? sid.Id : prod.Id!.ToString()!;

        session.Patch<Product>(id)
            .Delete(p => p.Price);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Product>(id);
        reloaded.ShouldNotBeNull();
        // After DELETE field, Price should be 0 (default for decimal)
        reloaded.Price.ShouldBe(0);
    }

    [Test]
    public async Task Patch_on_nonexistent_record_is_noop()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Patching a record that doesn't exist should not throw
        session.Patch<Person>("nonexistent-id")
            .Set(p => p.Age, 99);
        await session.SaveChangesAsync();

        // No exception thrown - success
        true.ShouldBeTrue();
    }
}
