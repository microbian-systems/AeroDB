using AeroDB.Sable;

namespace AeroDB.Tests;

public class AdvancedSdkTests
{
    [Test]
    public async Task Advanced_SurrealDbClient_NotNull()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        store.Advanced.Client.ShouldNotBeNull();
    }

    [Test]
    public async Task Advanced_CreateSession_ForkSession()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.Advanced.CreateSessionAsync();

        session.ShouldNotBeNull();
        session.SessionId.ShouldNotBeNull();

        // Verify the session can execute a query (proves it's a valid, independent session)
        var result = await session.RawQuery("RETURN 42");
        result.ShouldNotBeNull();
    }

    [Test]
    public async Task Advanced_Client_Is_Singleton()
    {
        await using var store = await TestHarness.CreateStoreAsync();

        var client1 = store.Advanced.Client;
        var client2 = store.Advanced.Client;

        client1.ShouldBeSameAs(client2);
    }

    [Test]
    public async Task StoreOptions_Advanced_Defaults()
    {
        await using var store = await TestHarness.CreateStoreAsync();

        store.Options.Advanced.SurrealDbClient.ShouldNotBeNull();
        store.Options.Advanced.CreateSessionAsync.ShouldNotBeNull();
    }

    [Test]
    public async Task Advanced_CreateSession_Returns_Valid_Session()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.Advanced.CreateSessionAsync();

        session.ShouldNotBeNull();
        session.SessionId.ShouldNotBeNull();
    }

    [Test]
    public async Task Advanced_Multiple_CreateSession_Calls()
    {
        await using var store = await TestHarness.CreateStoreAsync();

        await using var session1 = await store.Advanced.CreateSessionAsync();
        await using var session2 = await store.Advanced.CreateSessionAsync();

        session1.ShouldNotBeNull();
        session2.ShouldNotBeNull();

        // Sessions should be independent (different session IDs)
        session1.SessionId.ShouldNotBe(session2.SessionId);
    }

    [Test]
    public async Task Advanced_Session_Can_Query()
    {
        await using var store = await TestHarness.CreateStoreAsync();

        // Store some data first
        await using var writeSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        writeSession.Store(new Person { Name = "AdvTest", Age = 50, Email = "advtest@test.com" });
        await writeSession.SaveChangesAsync();

        // Query using an advanced session
        await using var advSession = await store.Advanced.CreateSessionAsync();
        var result = await advSession.RawQuery("SELECT * FROM person WHERE name = 'AdvTest'");
        result.ShouldNotBeNull();
        result.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task IAeroDBAdvanced_Is_Exposed_From_Store()
    {
        await using var store = await TestHarness.CreateStoreAsync();

        var advanced = store.Advanced;
        advanced.ShouldNotBeNull();
        advanced.ShouldBeAssignableTo<IAeroDBAdvanced>();
    }
}
