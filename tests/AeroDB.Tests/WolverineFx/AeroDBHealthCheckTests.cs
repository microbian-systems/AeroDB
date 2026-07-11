using AeroDB.WolverineFx;

namespace AeroDB.Tests;
using Sable;
using AeroDB.WolverineFx;
using NSubstitute;
using Shouldly;
using SurrealDb.Embedded.SurrealKv;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine.Transports;

public class AeroDBHealthCheckTests
{
    [Test]
    public async Task CheckHealthAsync_Healthy_ReturnsHealthy()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"AeroDB_health_test_{Guid.NewGuid():N}.db");
        var client = new SurrealDbKvClient(dbPath);
        await client.Use("test", "test");
        var store = Documents.For(o =>
        {
            o.ClientFactory = () => client;
        });
        await store.InitializeAsync();

        var healthCheck = new AeroDBHealthCheck(store);
        var result = await healthCheck.CheckHealthAsync();

        result.ShouldNotBeNull();
        result.Status.ShouldBe(TransportHealthStatus.Healthy);
        result.Message.ShouldNotBeNull().ShouldContain("AeroDB.Sable:reachable");
        result.TransportName.ShouldBe("AeroDB.Sable");

        await store.DisposeAsync();
    }

    [Test]
    public async Task CheckHealthAsync_Unreachable_ReturnsUnhealthy()
    {
        var mockStore = Substitute.For<IDocumentStore>();
        mockStore.QuerySessionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IQuerySession>(new InvalidOperationException("db down")));

        var healthCheck = new AeroDBHealthCheck(mockStore);
        var result = await healthCheck.CheckHealthAsync();

        result.ShouldNotBeNull();
        result.Status.ShouldBe(TransportHealthStatus.Unhealthy);
        result.Message!.ShouldContain("AeroDB.Sable:unreachable");
    }
}
