namespace Dali.Tests;

using Dali;
using Dali.WolverineFx;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using Wolverine.Runtime;
using Wolverine.Transports;

/// <summary>
/// Unit tests for <see cref="DaliTransport"/> — the ITransport implementation
/// that registers the "dali://" protocol scheme for Wolverine.
/// No Wolverine runtime or SurrealDB involved.
/// </summary>
public class DaliTransportTests
{
    [Test]
    public void Protocol_ReturnsDali()
    {
        var transport = new DaliTransport();

        transport.Protocol.ShouldBe("dali");
    }

    [Test]
    public void Name_ReturnsDescriptiveName()
    {
        var transport = new DaliTransport();

        transport.Name.ShouldBe("Dali SurrealDB Transport");
    }

    [Test]
    public void ReplyEndpoint_ReturnsNull()
    {
        var transport = new DaliTransport();

        transport.ReplyEndpoint().ShouldBeNull();
    }

    [Test]
    public void GetOrCreateEndpoint_ReturnsEndpoint_ForNewUri()
    {
        var transport = new DaliTransport();
        var uri = new Uri("dali://queue/incoming");

        var endpoint = transport.GetOrCreateEndpoint(uri);

        endpoint.ShouldNotBeNull();
        endpoint.ShouldBeOfType<DaliEndpoint>();
        endpoint.Uri.ShouldBe(uri);
    }

    [Test]
    public void GetOrCreateEndpoint_ReturnsExistingEndpoint_OnSecondCall()
    {
        var transport = new DaliTransport();
        var uri = new Uri("dali://queue/incoming");

        var first = transport.GetOrCreateEndpoint(uri);
        var second = transport.GetOrCreateEndpoint(uri);

        second.ShouldBeSameAs(first);
    }

    [Test]
    public void TryGetEndpoint_ReturnsNull_WhenNotFound()
    {
        var transport = new DaliTransport();
        var uri = new Uri("dali://queue/unknown");

        var endpoint = transport.TryGetEndpoint(uri);

        endpoint.ShouldBeNull();
    }

    [Test]
    public void TryGetEndpoint_ReturnsEndpoint_WhenExists()
    {
        var transport = new DaliTransport();
        var uri = new Uri("dali://queue/incoming");

        transport.GetOrCreateEndpoint(uri);
        var endpoint = transport.TryGetEndpoint(uri);

        endpoint.ShouldNotBeNull();
        endpoint.ShouldBeOfType<DaliEndpoint>();
    }

    [Test]
    public void Endpoints_ReturnsAllRegisteredEndpoints()
    {
        var transport = new DaliTransport();
        var uri1 = new Uri("dali://queue/one");
        var uri2 = new Uri("dali://queue/two");

        transport.GetOrCreateEndpoint(uri1);
        transport.GetOrCreateEndpoint(uri2);

        var endpoints = transport.Endpoints();

        endpoints.ShouldNotBeNull();
        endpoints.Count().ShouldBe(2);
    }

    [Test]
    public async Task InitializeAsync_CompletesWithoutThrowing()
    {
        var transport = new DaliTransport();
        var runtime = Substitute.For<IWolverineRuntime>();

        await transport.InitializeAsync(runtime);

        // No exception = success
    }

    [Test]
    public void TryBuildBrokerUsage_ReturnsFalse()
    {
        var transport = new DaliTransport();

        var result = transport.TryBuildBrokerUsage(out var description);

        result.ShouldBeFalse();
        description.ShouldBeNull();
    }

    [Test]
    public void Describe_ReturnsFormattedString()
    {
        var transport = new DaliTransport();

        var description = ((ITransport)transport).Describe();

        description.ShouldBe("Dali SurrealDB Transport (scheme 'dali')");
    }

    [Test]
    public void BuildHealthCheck_ReturnsDaliHealthCheck()
    {
        var mockStore = Substitute.For<IDocumentStore>();
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IDocumentStore)).Returns(mockStore);
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Services.Returns(services);

        var transport = new DaliTransport();
        var healthCheck = transport.BuildHealthCheck(runtime);

        healthCheck.ShouldNotBeNull();
        healthCheck.ShouldBeOfType<DaliHealthCheck>();
    }
}
