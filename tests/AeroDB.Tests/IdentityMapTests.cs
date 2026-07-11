using AeroDB.Sable;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

public class IdentityMapTests
{
    [Test]
    public async Task LightweightSession_NoIdentityMap_DifferentInstances()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var id = ((RecordIdOf<string>)stored[0].Id!).Id;

        var first = await session.LoadAsync<Person>(id);
        var second = await session.LoadAsync<Person>(id);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeFalse(); // No identity map
    }

    [Test]
    public async Task DocumentSession_IdentityMap_SameInstance()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.IdentityOnly });

        session.Store(new Person { Name = "Alice", Age = 30 });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var id = ((RecordIdOf<string>)stored[0].Id!).Id;

        var first = await session.LoadAsync<Person>(id);
        var second = await session.LoadAsync<Person>(id);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeTrue(); // Same instance from identity map
    }

    [Test]
    public async Task Eject_RemovesFromIdentityMap()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.IdentityOnly });

        session.Store(new Person { Name = "Alice", Age = 30 });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var id = ((RecordIdOf<string>)stored[0].Id!).Id;

        var first = await session.LoadAsync<Person>(id);
        first.ShouldNotBeNull();

        session.Eject<Person>(id);

        var second = await session.LoadAsync<Person>(id);
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeFalse(); // Different instance after eject
    }

    [Test]
    public async Task ClearChanges_ClearsIdentityMap()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.IdentityOnly });

        session.Store(new Person { Name = "Alice", Age = 30 });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var id = ((RecordIdOf<string>)stored[0].Id!).Id;

        var first = await session.LoadAsync<Person>(id);
        first.ShouldNotBeNull();

        session.ClearChanges(); // Should eject everything from identity map

        var second = await session.LoadAsync<Person>(id);
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeFalse(); // Different instance after clear
    }

    [Test]
    public async Task EjectAll_ClearsAllTypes()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.IdentityOnly });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var aliceId = ((RecordIdOf<string>)stored[0].Id!).Id;
        var bobId = ((RecordIdOf<string>)stored[1].Id!).Id;

        var alice1 = await session.LoadAsync<Person>(aliceId);
        var bob1 = await session.LoadAsync<Person>(bobId);
        alice1.ShouldNotBeNull();
        bob1.ShouldNotBeNull();

        session.EjectAll(); // Clear all identity maps across all types

        var alice2 = await session.LoadAsync<Person>(aliceId);
        var bob2 = await session.LoadAsync<Person>(bobId);
        alice2.ShouldNotBeNull();
        bob2.ShouldNotBeNull();
        ReferenceEquals(alice1, alice2).ShouldBeFalse();
        ReferenceEquals(bob1, bob2).ShouldBeFalse();
    }

    [Test]
    public async Task IdentityMap_MultipleTypes_Independent()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.IdentityOnly });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Product { Name = "Widget", Price = 9.99m, Quantity = 10 });
        await session.SaveChangesAsync();

        var people = await session.Query<Person>().ToListAsync();
        var products = await session.Query<Product>().ToListAsync();
        var personId = ((RecordIdOf<string>)people[0].Id!).Id;
        var productId = ((RecordIdOf<string>)products[0].Id!).Id;

        // Load Person twice — identity map should return same instance
        var person1 = await session.LoadAsync<Person>(personId);
        var person2 = await session.LoadAsync<Person>(personId);
        person1.ShouldNotBeNull();
        person2.ShouldNotBeNull();
        ReferenceEquals(person1, person2).ShouldBeTrue();

        // Load Product twice — identity map should return same instance
        var product1 = await session.LoadAsync<Product>(productId);
        var product2 = await session.LoadAsync<Product>(productId);
        product1.ShouldNotBeNull();
        product2.ShouldNotBeNull();
        ReferenceEquals(product1, product2).ShouldBeTrue();

        // Verify identity maps are independent — ejecting Person should not affect Product
        session.Eject<Person>(personId);

        var person3 = await session.LoadAsync<Person>(personId);
        person3.ShouldNotBeNull();
        ReferenceEquals(person1, person3).ShouldBeFalse();

        var product3 = await session.LoadAsync<Product>(productId);
        product3.ShouldNotBeNull();
        ReferenceEquals(product1, product3).ShouldBeTrue(); // Product still tracked
    }

    [Test]
    public async Task DocumentSession_IdentityMap_AfterStoreAndQuery()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.IdentityOnly });

        // Store two entities so we have data
        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        // Query to retrieve stored entities and grab an ID
        var stored = await session.Query<Person>().ToListAsync();
        var id = ((RecordIdOf<string>)stored[0].Id!).Id;

        // First LoadAsync populates the identity map
        var first = await session.LoadAsync<Person>(id);
        first.ShouldNotBeNull();
        first.Name.ShouldBeOneOf("Alice", "Bob");

        // Second LoadAsync should hit the identity map
        var second = await session.LoadAsync<Person>(id);
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Test]
    public async Task LightweightSession_EjectDoesNotAffectBehavior()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var id = ((RecordIdOf<string>)stored[0].Id!).Id;

        var first = await session.LoadAsync<Person>(id);
        first.ShouldNotBeNull();

        // Eject on LightweightSession should not throw
        Should.NotThrow(() => session.Eject<Person>(id));

        // After Eject, lightweight session should still return different instances
        // (it has no identity map, so every LoadAsync makes a fresh fetch)
        var second = await session.LoadAsync<Person>(id);
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeFalse();
    }
}
