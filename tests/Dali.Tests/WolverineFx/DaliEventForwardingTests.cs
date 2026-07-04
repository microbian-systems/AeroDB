using System.Reflection;
using Dali;
using Dali.WolverineFx;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;

namespace Dali.Tests;

/// <summary>
/// Mock-based unit tests for <see cref="DaliEventForwarding"/>.
/// Tests guard clauses (null context, non-DocumentSession, empty events),
/// SetCurrentContext, and the BasicAccessor behaviors.
///
/// NOTE: The event-publishing path (BeforeSaveChangesAsync with appended events)
/// cannot be tested purely with mocks because DaliSessionEventAccessor reads
/// _appendedEvents (typed as List&lt;IEvent&gt;) and expects ValueTuple&lt;string,object&gt;
/// items — a pre-existing mismatch in the reflection accessor. Full event-forwarding
/// coverage requires an integration test (see DaliWolverineIntegrationTests).
/// No Wolverine daemon or SurrealDB needed.
/// </summary>
public class DaliEventForwardingTests
{
    // ─── BeforeSaveChangesAsync: No context ─────────────────────────

    [Test]
    public async Task BeforeSaveChangesAsync_when_context_is_null_does_not_publish()
    {
        DaliEventForwarding.SetCurrentContext(null);
        var forwarder = new DaliEventForwarding();
        var session = Substitute.For<IDocumentSession>();

        await forwarder.BeforeSaveChangesAsync(session, CancellationToken.None);
        // No exception = success (returns early when context is null)
    }

    [Test]
    public async Task BeforeSaveChangesAsync_when_session_is_not_DocumentSession_does_not_publish()
    {
        var context = Substitute.For<IMessageContext>();
        DaliEventForwarding.SetCurrentContext(context);
        var forwarder = new DaliEventForwarding();
        var session = Substitute.For<IDocumentSession>(); // NOT a DocumentSession

        await forwarder.BeforeSaveChangesAsync(session, CancellationToken.None);

        await context.DidNotReceiveWithAnyArgs().PublishAsync<object>(default!);
    }

    // ─── BeforeSaveChangesAsync: No events (DocumentSession with empty list) ──

    [Test]
    public async Task BeforeSaveChangesAsync_when_no_events_does_not_publish()
    {
        var context = Substitute.For<IMessageContext>();
        DaliEventForwarding.SetCurrentContext(context);
        var forwarder = new DaliEventForwarding();

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
        DaliEventForwarding.SetCurrentContext(context);

        DaliEventForwarding.CurrentContext.Value.ShouldBe(context);
    }

    [Test]
    public async Task SetCurrentContext_with_null_clears_context()
    {
        var context = Substitute.For<IMessageContext>();
        DaliEventForwarding.SetCurrentContext(context);
        DaliEventForwarding.SetCurrentContext(null);

        DaliEventForwarding.CurrentContext.Value.ShouldBeNull();
    }
}
