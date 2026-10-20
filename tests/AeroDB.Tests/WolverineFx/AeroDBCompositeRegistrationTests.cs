using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;

namespace AeroDB.Tests;

public sealed class AeroDBCompositeRegistrationTests
{
    [Test]
    public async Task Composite_registration_resolves_the_borrowed_main_store_from_document_store_without_ambient_client_di()
    {
        var documentStore = DocumentStoreWithClient(out var documentClient);
        var options = new WolverineOptions();
        options.Services.AddSingleton(documentStore);
        options.Services.AddLogging();
        options.PersistMessagesWithAeroDBTenancy(
            Substitute.For<IAeroDBTenantStoreDirectory>(),
            Substitute.For<IAeroDBTenantDocumentStoreFactory>());

        await using var provider = options.Services.BuildServiceProvider();

        var composite = provider.GetRequiredService<AeroDBCompositeMainMessageStore>();
        ((AeroDBMessageStore)composite.Nodes).Client.ShouldBeSameAs(documentClient);
    }

    [Test]
    public async Task Composite_registration_uses_the_document_store_client_when_ambient_client_differs_and_borrows_both()
    {
        var documentStore = DocumentStoreWithClient(out var documentClient);
        var ambientClient = Substitute.For<ISurrealDbClient>();
        var options = new WolverineOptions();
        options.Services.AddSingleton(documentStore);
        options.Services.AddSingleton(ambientClient);
        options.Services.AddLogging();
        options.PersistMessagesWithAeroDBTenancy(
            Substitute.For<IAeroDBTenantStoreDirectory>(),
            Substitute.For<IAeroDBTenantDocumentStoreFactory>());

        await using (var provider = options.Services.BuildServiceProvider())
        {
            var composite = provider.GetRequiredService<AeroDBCompositeMainMessageStore>();

            ((AeroDBMessageStore)composite.Nodes).Client.ShouldBeSameAs(documentClient);
            ((AeroDBMessageStore)composite.Nodes).Client.ShouldNotBeSameAs(ambientClient);

            await composite.DisposeAsync();
        }

        await documentClient.DidNotReceive().DisposeAsync();
        await ambientClient.DidNotReceive().DisposeAsync();
    }

    [Test]
    public void Composite_registration_adds_one_composite_message_store_without_a_direct_agent_family()
    {
        var options = new WolverineOptions();

        options.PersistMessagesWithAeroDBTenancy(
            Substitute.For<IAeroDBTenantStoreDirectory>(),
            Substitute.For<IAeroDBTenantDocumentStoreFactory>());

        options.Services.Count(x => x.ServiceType == typeof(IMessageStore)).ShouldBe(1);
        options.Services.ShouldNotContain(x => x.ServiceType == typeof(IAgentFamily));
        options.Services.ShouldNotContain(x => x.ServiceType == typeof(IWolverineExtension));
        options.Services.ShouldNotContain(x => x.ServiceType == typeof(AeroDBMessageStore));
        options.Transports.OfType<AeroDBTransport>().ShouldBeEmpty();
    }

    [Test]
    public void Composite_registration_rejects_repeated_and_legacy_aerodb_registration()
    {
        var composite = new WolverineOptions();
        composite.PersistMessagesWithAeroDBTenancy(
            Substitute.For<IAeroDBTenantStoreDirectory>(),
            Substitute.For<IAeroDBTenantDocumentStoreFactory>());

        Should.Throw<InvalidOperationException>(() => composite.PersistMessagesWithAeroDBTenancy(
            Substitute.For<IAeroDBTenantStoreDirectory>(),
            Substitute.For<IAeroDBTenantDocumentStoreFactory>()));

        var legacy = new WolverineOptions();
        legacy.PersistMessagesWithAeroDB(
            Substitute.For<SurrealDb.Net.ISurrealDbClient>(),
            Substitute.For<ILogger<AeroDBMessageStore>>());

        Should.Throw<InvalidOperationException>(() => legacy.PersistMessagesWithAeroDBTenancy(
            Substitute.For<IAeroDBTenantStoreDirectory>(),
            Substitute.For<IAeroDBTenantDocumentStoreFactory>()));

        Should.Throw<InvalidOperationException>(() => composite.PersistMessagesWithAeroDB(
            Substitute.For<SurrealDb.Net.ISurrealDbClient>(),
            Substitute.For<ILogger<AeroDBMessageStore>>()));
    }

    [Test]
    public void Existing_legacy_registration_remains_repeatable_without_the_composite()
    {
        var options = new WolverineOptions();

        options.PersistMessagesWithAeroDB(
            Substitute.For<SurrealDb.Net.ISurrealDbClient>(),
            Substitute.For<ILogger<AeroDBMessageStore>>());
        Should.NotThrow(() => options.PersistMessagesWithAeroDB(
            Substitute.For<SurrealDb.Net.ISurrealDbClient>(),
            Substitute.For<ILogger<AeroDBMessageStore>>()));
    }

    private static IDocumentStore DocumentStoreWithClient(out ISurrealDbClient client)
    {
        client = Substitute.For<ISurrealDbClient>();
        var documentStore = Substitute.For<IDocumentStore>();
        documentStore.Client.Returns(client);
        documentStore.Options.Returns(new StoreOptions
        {
            Endpoint = "ws://document-store.test/rpc",
            Namespace = "coordinator",
            Database = "main"
        });
        return documentStore;
    }
}
