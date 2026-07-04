namespace AeroDB.Tests;

using AeroDB;
using AeroDB.WolverineFx;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using Wolverine.Runtime;
using Wolverine.Transports;

/// <summary>
/// Unit tests for <see cref="AeroDBTransport"/> — the ITransport implementation
/// that registers the "AeroDB://" protocol scheme for Wolverine.
/// No Wolverine runtime or SurrealDB involved.
/// </summary>
public class AeroDBTransportTests
{
    [Test]
    public void Protocol_ReturnsAeroDB()
    {
        var transport = new AeroDBTransport();

        transport.Protocol.ShouldBe("AeroDB");
    }

    [Test]
    public void Name_ReturnsDescriptiveName()
    {
        var transport = new AeroDBTransport();

        transport.Name.ShouldBe("AeroDB SurrealDB Transport");
    }

    [Test]
    public void ReplyEndpoint_ReturnsNull()
    {
        var transport = new AeroDBTransport();

        transport.ReplyEndpoint().ShouldBeNull();
    }

    [Test]
    public void GetOrCreateEndpoint_ReturnsEndpoint_ForNewUri()
    {
        var transport = new AeroDBTransport();
        var uri = new Uri("AeroDB://queue/incoming");

        var endpoint = transport.GetOrCreateEndpoint(uri);

        endpoint.ShouldNotBeNull();
        endpoint.ShouldBeOfType<AeroDBEndpoint>();
        endpoint.Uri.ShouldBe(uri);
    }

    [Test]
    public void GetOrCreateEndpoint_ReturnsExistingEndpoint_OnSecondCall()
    {
        var transport = new AeroDBTransport();
        var uri = new Uri("AeroDB://queue/incoming");

        var first = transport.GetOrCreateEndpoint(uri);
        var second = transport.GetOrCreateEndpoint(uri);

        second.ShouldBeSameAs(first);
    }

    [Test]
    public void TryGetEndpoint_ReturnsNull_WhenNotFound()
    {
        var transport = new AeroDBTransport();
        var uri = new Uri("AeroDB://queue/unknown");

        var endpoint = transport.TryGetEndpoint(uri);

        endpoint.ShouldBeNull();
    }

    [Test]
    public void TryGetEndpoint_ReturnsEndpoint_WhenExists()
    {
        var transport = new AeroDBTransport();
        var uri = new Uri("AeroDB://queue/incoming");

        transport.GetOrCreateEndpoint(uri);
        var endpoint = transport.TryGetEndpoint(uri);

        endpoint.ShouldNotBeNull();
        endpoint.ShouldBeOfType<AeroDBEndpoint>();
    }

    [Test]
    public void Endpoints_ReturnsAllRegisteredEndpoints()
    {
        var transport = new AeroDBTransport();
        var uri1 = new Uri("AeroDB://queue/one");
        var uri2 = new Uri("AeroDB://queue/two");

        transport.GetOrCreateEndpoint(uri1);
        transport.GetOrCreateEndpoint(uri2);

        var endpoints = transport.Endpoints();

        endpoints.ShouldNotBeNull();
        endpoints.Count().ShouldBe(2);
    }

    [Test]
    public async Task InitializeAsync_CompletesWithoutThrowing()
    {
        var transport = new AeroDBTransport();
        var runtime = Substitute.For<IWolverineRuntime>();

        await transport.InitializeAsync(runtime);

        // No exception = success
    }

    [Test]
    public void TryBuildBrokerUsage_ReturnsFalse()
    {
        var transport = new AeroDBTransport();

        var result = transport.TryBuildBrokerUsage(out var description);

        result.ShouldBeFalse();
        description.ShouldBeNull();
    }

    [Test]
    public void Describe_ReturnsFormattedString()
    {
        var transport = new AeroDBTransport();

        var description = ((ITransport)transport).Describe();

        description.ShouldBe("AeroDB SurrealDB Transport (scheme 'AeroDB')");
    }

    [Test]
    public void BuildHealthCheck_ReturnsAeroDBHealthCheck()
    {
        var mockStore = Substitute.For<IDocumentStore>();
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IDocumentStore)).Returns(mockStore);
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Services.Returns(services);

        var transport = new AeroDBTransport();
        var healthCheck = transport.BuildHealthCheck(runtime);

        healthCheck.ShouldNotBeNull();
        healthCheck.ShouldBeOfType<AeroDBHealthCheck>();
    }
}
