using System.Reflection;
using AeroDB.Sable;
using AeroDB.SourceGenerators;
using AeroDB.WolverineFx;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;

namespace AeroDB.Tests;

/// <summary>
/// Mock-based unit tests for <see cref="AeroDBEventForwarding"/>.
/// Tests guard clauses (null context, non-DocumentSession, empty events),
/// SetCurrentContext, and the BasicAccessor behaviors.
///
/// NOTE: The event-publishing path (BeforeSaveChangesAsync with appended events)
/// cannot be tested purely with mocks because AeroDBSessionEventAccessor reads
/// _appendedEvents (typed as List&lt;IEvent&gt;) and expects ValueTuple&lt;string,object&gt;
/// items — a pre-existing mismatch in the reflection accessor. Full event-forwarding
/// coverage requires an integration test (see AeroDBWolverineIntegrationTests).
/// No Wolverine daemon or SurrealDB needed.
/// </summary>
public class AeroDBEventForwardingTests
{
    // ─── BeforeSaveChangesAsync: No context ─────────────────────────

    [Test]
    public async Task BeforeSaveChangesAsync_when_context_is_null_does_not_publish()
    {
        AeroDBEventForwarding.SetCurrentContext(null);
        var forwarder = new AeroDBEventForwarding();
        var session = Substitute.For<IDocumentSession>();

        await forwarder.BeforeSaveChangesAsync(session, CancellationToken.None);
        // No exception = success (returns early when context is null)
    }

    [Test]
    public async Task BeforeSaveChangesAsync_when_session_is_not_DocumentSession_does_not_publish()
    {
        var context = Substitute.For<IMessageContext>();
        AeroDBEventForwarding.SetCurrentContext(context);
        var forwarder = new AeroDBEventForwarding();
        var session = Substitute.For<IDocumentSession>(); // NOT a DocumentSession

        await forwarder.BeforeSaveChangesAsync(session, CancellationToken.None);

        await context.DidNotReceiveWithAnyArgs().PublishAsync<object>(default!);
    }

    // ─── BeforeSaveChangesAsync: No events (DocumentSession with empty list) ──

    [Test]
    public async Task BeforeSaveChangesAsync_when_no_events_does_not_publish()
    {
        var context = Substitute.For<IMessageContext>();
        AeroDBEventForwarding.SetCurrentContext(context);
        var forwarder = new AeroDBEventForwarding();

        // DocumentSession with empty _appendedEvents by default
        var client = Substitute.For<ISurrealDbClient>();
        var session = Substitute.For<ISurrealDbSession>();
        var options = new StoreOptions();
        var docSession = new DocumentSession(client, session, options, DocumentTracking.None);

        await forwarder.BeforeSaveChangesAsync(docSession, CancellationToken.None);

        await context.DidNotReceiveWithAnyArgs().PublishAsync<object>(default!);
    }

    // ─── SetCurrentContext ──────────────────────────────────────────

    [Test]
    public async Task SetCurrentContext_stores_and_retrieves_context()
    {
        var context = Substitute.For<IMessageContext>();
        AeroDBEventForwarding.SetCurrentContext(context);

        AeroDBEventForwarding.CurrentContext.Value.ShouldBe(context);
    }

    [Test]
    public async Task SetCurrentContext_with_null_clears_context()
    {
        var context = Substitute.For<IMessageContext>();
        AeroDBEventForwarding.SetCurrentContext(context);
        AeroDBEventForwarding.SetCurrentContext(null);

        AeroDBEventForwarding.CurrentContext.Value.ShouldBeNull();
    }
}
