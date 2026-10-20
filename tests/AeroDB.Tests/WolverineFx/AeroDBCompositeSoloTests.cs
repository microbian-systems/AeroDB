using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using SurrealDb.Embedded.InMemory;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace AeroDB.Tests;

[NotInParallel]
public sealed class AeroDBCompositeSoloTests
{
    [Test]
    public async Task Solo_host_discovers_main_and_tenants_without_exposing_a_raw_main_store()
    {
        var directory = new Directory([Location("alpha")]);
        await using var fixture = await Fixture.CreateAsync(directory);

        var store = fixture.Host.Services.GetRequiredService<IMessageStore>();
        var source = fixture.Host.Services.GetRequiredService<AeroDBTenantedMessageSource>();
        var runtime = fixture.Host.Services.GetRequiredService<IWolverineRuntime>();

        store.ShouldBeOfType<MultiTenantedMessageStore>();
        store.Role.ShouldBe(MessageStoreRole.Composite);
        source.AllActiveByTenant().Select(x => x.TenantId).ShouldBe(["alpha"]);
        runtime.Options.Durability.AssignedNodeNumber.ShouldBe(1);
        fixture.Host.Services.GetService<AeroDBMessageStore>().ShouldBeNull();

        directory.Set([Location("alpha"), Location("beta")]);
        await source.RefreshAsync();

        source.AllActiveByTenant().Select(x => x.TenantId).ShouldBe(["alpha", "beta"]);
        source.AllActive().Select(x => x.Uri).Distinct().Count().ShouldBe(2);
    }

    private static AeroDBTenantStoreLocation Location(string tenant) =>
        new(tenant, "test", "ca_" + tenant, "main");

    private sealed class Directory(IReadOnlyList<AeroDBTenantStoreLocation> initial) : IAeroDBTenantStoreDirectory
    {
        private IReadOnlyList<AeroDBTenantStoreLocation> _locations = initial;
        public void Set(IReadOnlyList<AeroDBTenantStoreLocation> locations) => _locations = locations;
        public ValueTask<IReadOnlyList<AeroDBTenantStoreLocation>> ListActiveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_locations);
    }

    private sealed class DocumentsFactory : IAeroDBTenantDocumentStoreFactory
    {
        public ValueTask<IDocumentStore> CreateAsync(AeroDBTenantStoreLocation location, CancellationToken cancellationToken = default)
        {
            var client = new SurrealDbMemoryClient();
            var store = Documents.For(options =>
            {
                options.ClientFactory = () => client;
                options.Namespace = location.Namespace;
                options.Database = location.Database;
            });
            return ValueTask.FromResult(store);
        }
    }

    private sealed class Fixture(IHost host, IDocumentStore documents, ISurrealDbClient client) : IAsyncDisposable
    {
        public IHost Host { get; } = host;

        public static async Task<Fixture> CreateAsync(Directory directory)
        {
            var client = new SurrealDbMemoryClient();
            await client.Use("aero", "main");
            var documents = Documents.For(options =>
            {
                options.ClientFactory = () => client;
                options.Namespace = "aero";
                options.Database = "main";
            });
            await documents.InitializeAsync();

            var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(documents);
                    services.AddSingleton<ISurrealDbClient>(client);
                })
                .UseWolverine(options =>
                {
                    options.RestoreV5Defaults();
                    options.Durability.Mode = DurabilityMode.Solo;
                    options.PersistMessagesWithAeroDBTenancy(directory, new DocumentsFactory());
                })
                .Build();

            await host.StartAsync();
            return new Fixture(host, documents, client);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Host.Dispose();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.ToString());
                throw;
            }
            finally
            {
                await documents.DisposeAsync();
                await client.DisposeAsync();
            }
        }
    }
}
