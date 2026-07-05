using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

internal class TestSeeder : IInitialData
{
    public static bool WasCalled { get; set; }

    public async Task PopulateAsync(IDocumentSession session, CancellationToken ct)
    {
        WasCalled = true;
        var person = new Person { Name = "SeededUser", Age = 25 };
        session.Store(person);
    }
}

public class InitialDataTests
{
    [Test]
    public async Task IInitialData_seeder_runs_during_initialize()
    {
        TestSeeder.WasCalled = false;

        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Namespace = "test";
            o.Database = "test";
        });
        store.Options.InitialData.Add(new TestSeeder());

        await store.InitializeAsync();

        TestSeeder.WasCalled.ShouldBeTrue();

        await using var query = await store.QuerySessionAsync();
        var people = await query.Query<Person>().ToListAsync();
        people.Any(p => p.Name == "SeededUser").ShouldBeTrue();
    }
}
