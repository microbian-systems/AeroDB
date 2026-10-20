using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;

namespace AeroDB.Tests;

public sealed class AeroDBCompositeDiRegistrationTests
{
    [Test]
    public async Task Generic_registration_constructs_tenant_routing_from_the_owning_container_and_disposes_each_once()
    {
        var dependency = new RoutingDependency();
        var documentStore = Substitute.For<IDocumentStore>();
        var options = new WolverineOptions();
        options.Services.AddSingleton(dependency);
        options.Services.AddSingleton(documentStore);
        options.Services.AddLogging();

        options.PersistMessagesWithAeroDBTenancy<ContainerDirectory, ContainerDocumentStoreFactory>();

        var directory = default(ContainerDirectory);
        var factory = default(ContainerDocumentStoreFactory);
        await using (var provider = options.Services.BuildServiceProvider())
        {
            directory = (ContainerDirectory)provider.GetRequiredService<IAeroDBTenantStoreDirectory>();
            factory = (ContainerDocumentStoreFactory)provider.GetRequiredService<IAeroDBTenantDocumentStoreFactory>();

            directory.Dependency.ShouldBeSameAs(dependency);
            factory.Dependency.ShouldBeSameAs(dependency);
            provider.GetRequiredService<IAeroDBTenantStoreDirectory>().ShouldBeSameAs(directory);
            provider.GetRequiredService<IAeroDBTenantDocumentStoreFactory>().ShouldBeSameAs(factory);
            provider.GetService<ContainerDirectory>().ShouldBeNull();
            provider.GetService<ContainerDocumentStoreFactory>().ShouldBeNull();
        }

        directory!.DisposeCount.ShouldBe(1);
        factory!.DisposeCount.ShouldBe(1);
    }

    [Test]
    public async Task Instance_registration_leaves_caller_owned_tenant_routing_instances_undisposed()
    {
        var directory = new ContainerDirectory(new RoutingDependency());
        var factory = new ContainerDocumentStoreFactory(new RoutingDependency());
        var options = new WolverineOptions();
        options.Services.AddSingleton(Substitute.For<IDocumentStore>());
        options.Services.AddLogging();

        options.PersistMessagesWithAeroDBTenancy(directory, factory);

        await using (var provider = options.Services.BuildServiceProvider())
        {
            provider.GetRequiredService<IAeroDBTenantStoreDirectory>().ShouldBeSameAs(directory);
            provider.GetRequiredService<IAeroDBTenantDocumentStoreFactory>().ShouldBeSameAs(factory);
        }

        directory.DisposeCount.ShouldBe(0);
        factory.DisposeCount.ShouldBe(0);
    }

    [Test]
    public void Generic_registration_reports_a_missing_container_dependency_when_tenant_routing_is_resolved()
    {
        var options = new WolverineOptions();
        options.Services.AddSingleton(Substitute.For<IDocumentStore>());
        options.Services.AddLogging();
        options.PersistMessagesWithAeroDBTenancy<ContainerDirectory, ContainerDocumentStoreFactory>();

        using var provider = options.Services.BuildServiceProvider();

        var exception = Should.Throw<InvalidOperationException>(
            () => provider.GetRequiredService<IAeroDBTenantStoreDirectory>());

        exception.Message.ShouldContain(nameof(RoutingDependency));
    }

    [Test]
    public void Generic_registration_reports_a_missing_container_dependency_during_service_provider_validation()
    {
        var options = new WolverineOptions();
        options.Services.AddSingleton(Substitute.For<IDocumentStore>());
        options.Services.AddLogging();
        options.PersistMessagesWithAeroDBTenancy<ContainerDirectory, ContainerDocumentStoreFactory>();

        var exception = Should.Throw<AggregateException>(() =>
            options.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }));

        exception.ToString().ShouldContain(nameof(RoutingDependency));
    }

    [Test]
    public void Generic_registration_rejects_existing_directory_registration_without_partially_registering_composite_services()
    {
        var options = new WolverineOptions();
        options.Services.AddSingleton<IAeroDBTenantStoreDirectory>(new ContainerDirectory(new RoutingDependency()));
        var serviceCount = options.Services.Count;

        var exception = Should.Throw<InvalidOperationException>(
            () => options.PersistMessagesWithAeroDBTenancy<ContainerDirectory, ContainerDocumentStoreFactory>());

        exception.Message.ShouldContain(nameof(IAeroDBTenantStoreDirectory));
        options.Services.Count.ShouldBe(serviceCount);
        options.Services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IMessageStore));
    }

    [Test]
    public void Generic_registration_rejects_existing_directory_implementation_registration_without_partially_registering_composite_services()
    {
        var options = new WolverineOptions();
        options.Services.AddSingleton(new ContainerDirectory(new RoutingDependency()));
        var serviceCount = options.Services.Count;

        var exception = Should.Throw<InvalidOperationException>(
            () => options.PersistMessagesWithAeroDBTenancy<ContainerDirectory, ContainerDocumentStoreFactory>());

        exception.Message.ShouldContain(nameof(ContainerDirectory));
        options.Services.Count.ShouldBe(serviceCount);
        options.Services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IMessageStore));
    }

    [Test]
    public void Generic_registration_rejects_existing_factory_implementation_registration_without_partially_registering_composite_services()
    {
        var options = new WolverineOptions();
        options.Services.AddSingleton(new ContainerDocumentStoreFactory(new RoutingDependency()));
        var serviceCount = options.Services.Count;

        var exception = Should.Throw<InvalidOperationException>(
            () => options.PersistMessagesWithAeroDBTenancy<ContainerDirectory, ContainerDocumentStoreFactory>());

        exception.Message.ShouldContain(nameof(ContainerDocumentStoreFactory));
        options.Services.Count.ShouldBe(serviceCount);
        options.Services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IMessageStore));
    }

    [Test]
    public void Instance_registration_rejects_existing_factory_registration_without_partially_registering_composite_services()
    {
        var options = new WolverineOptions();
        options.Services.AddSingleton<IAeroDBTenantDocumentStoreFactory>(new ContainerDocumentStoreFactory(new RoutingDependency()));
        var serviceCount = options.Services.Count;

        var exception = Should.Throw<InvalidOperationException>(() => options.PersistMessagesWithAeroDBTenancy(
            new ContainerDirectory(new RoutingDependency()),
            new ContainerDocumentStoreFactory(new RoutingDependency())));

        exception.Message.ShouldContain(nameof(IAeroDBTenantDocumentStoreFactory));
        options.Services.Count.ShouldBe(serviceCount);
        options.Services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IMessageStore));
    }

    private sealed class RoutingDependency;

    private sealed class ContainerDirectory(RoutingDependency dependency) : IAeroDBTenantStoreDirectory, IDisposable
    {
        public RoutingDependency Dependency { get; } = dependency;
        public int DisposeCount { get; private set; }

        public ValueTask<IReadOnlyList<AeroDBTenantStoreLocation>> ListActiveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AeroDBTenantStoreLocation>>(Array.Empty<AeroDBTenantStoreLocation>());

        public void Dispose() => DisposeCount++;
    }

    private sealed class ContainerDocumentStoreFactory(RoutingDependency dependency) : IAeroDBTenantDocumentStoreFactory, IDisposable
    {
        public RoutingDependency Dependency { get; } = dependency;
        public int DisposeCount { get; private set; }

        public ValueTask<IDocumentStore> CreateAsync(AeroDBTenantStoreLocation location, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose() => DisposeCount++;
    }
}
