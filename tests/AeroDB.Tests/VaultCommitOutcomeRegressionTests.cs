using AeroDB.Sable;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SurrealDb.Net;
using SurrealDb.Net.Exceptions;
using SurrealDb.Net.Internals;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>Uses the real driver transaction over a substituted transport; no network or database.</summary>
public sealed class VaultCommitOutcomeRegressionTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Indeterminate_commit_is_a_distinct_fault_without_retry_or_rollback(
        bool explicitTransaction, bool cancellationAtCommit)
    {
        using var cancellation = new CancellationTokenSource();
        var engine = CreateEngine();
        engine.Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (!cancellationAtCommit)
                    return Task.FromException(new TimeoutException("synthetic lost commit acknowledgement"));
                cancellation.Cancel();
                return Task.FromCanceled(call.ArgAt<CancellationToken>(2));
            });
        var listener = new CommitObserver();
        await using var client = new TransportClient(engine);
        var transportSession = await client.CreateSession();
        var options = new StoreOptions();
        options.Listeners.Add(listener);
        await using var session = new DocumentSession(client, transportSession, options, DocumentTracking.None);
        if (explicitTransaction) await session.BeginTransactionAsync();
        session.Store(new Person { Id = RecordId.From("person", "stable-commit-operation"), Name = "synthetic" });
        if (explicitTransaction) await session.SaveChangesAsync(cancellation.Token);

        var operation = explicitTransaction
            ? session.CommitTransactionAsync(cancellation.Token)
            : session.SaveChangesAsync(cancellation.Token);
        var exception = await Should.ThrowAsync<Exception>(() => operation);

        exception.ShouldBeOfType<CommitOutcomeUnknownException>();
        exception.InnerException.ShouldNotBeNull();
        if (cancellationAtCommit)
            exception.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        else
            exception.InnerException.ShouldBeOfType<TimeoutException>();
        operation.IsFaulted.ShouldBeTrue();
        operation.IsCanceled.ShouldBeFalse();
        listener.AfterCommitCalls.ShouldBe(0);
        await engine.Received(1).Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), cancellation.Token);
        await engine.DidNotReceive().Cancel(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await engine.Received(1).RawQuery(Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<Guid?>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Postcommit_listener_failure_is_not_an_unknown_database_commit()
    {
        var engine = CreateEngine();
        var listener = new FailingAfterCommitListener();
        await using var client = new TransportClient(engine);
        var transportSession = await client.CreateSession();
        var options = new StoreOptions();
        options.Listeners.Add(listener);
        await using var session = new DocumentSession(client, transportSession, options, DocumentTracking.None);
        session.Store(new Person { Id = RecordId.From("person", "known-success"), Name = "synthetic" });

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());

        exception.GetType().Name.ShouldNotBe("CommitOutcomeUnknownException");
        listener.AfterCommitCalls.ShouldBe(1);
        await engine.Received(1).Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await engine.DidNotReceive().Cancel(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task Confirmed_commit_with_callback_failure_is_a_known_commit_fault_not_cancellation(
        bool explicitTransaction, bool cancellationInCallback, bool sessionListener)
    {
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancellationInCallback
            ? new OperationCanceledException("synthetic private callback detail", cancellation.Token)
            : new InvalidOperationException("synthetic private callback detail");
        var engine = CreateEngine();
        engine.Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var listener = new ObservedCallbackFailure(cause, cancellationInCallback ? cancellation : null);
        await using var client = new TransportClient(engine);
        var options = new StoreOptions();
        if (!sessionListener) options.Listeners.Add(listener);
        await using var session = new DocumentSession(client, await client.CreateSession(), options, DocumentTracking.None);
        if (sessionListener) session.Listeners.Add(listener);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync() : null;
        session.Store(new Person { Id = RecordId.From("person", "confirmed-callback-operation"), Name = "synthetic" });
        if (explicitTransaction) await session.SaveChangesAsync(cancellation.Token);

        var operation = explicitTransaction
            ? sessionListener ? transaction!.CommitAsync(cancellation.Token) : session.CommitTransactionAsync(cancellation.Token)
            : session.SaveChangesAsync(cancellation.Token);
        var exception = await Should.ThrowAsync<Exception>(() => operation);

        exception.ShouldBeOfType<PostCommitCallbackException>().DatabaseCommitSucceeded.ShouldBeTrue();
        exception.ShouldBeAssignableTo<InvalidOperationException>();
        exception.InnerException.ShouldBeSameAs(cause);
        exception.Message.ShouldNotContain("synthetic private callback detail");
        operation.IsFaulted.ShouldBeTrue();
        operation.IsCanceled.ShouldBeFalse();
        if (cancellationInCallback)
            ((OperationCanceledException)exception.InnerException!).CancellationToken.ShouldBe(cancellation.Token);
        listener.AfterCommitCalls.ShouldBe(1);
        if (transaction is not null) await transaction.DisposeAsync();
        await engine.Received(1).Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), cancellation.Token);
        await engine.DidNotReceive().Cancel(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await engine.Received(1).RawQuery(Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<Guid?>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Confirmed_commit_callback_fault_preserves_dirty_tracking_success_cleanup(bool callbackCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        Exception cause = callbackCancellation
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("synthetic callback fault");
        var engine = CreateEngine();
        engine.Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        await using var client = new TransportClient(engine);
        var options = new StoreOptions { Tracking = DocumentTracking.DirtyTracking };
        options.Listeners.Add(new ObservedCallbackFailure(cause, callbackCancellation ? cancellation : null));
        await using var session = new DocumentSession(client, await client.CreateSession(), options, DocumentTracking.DirtyTracking);
        session.Store(new Person { Id = RecordId.From("person", "callback-cleanup"), Name = "synthetic" });
        session.IdentityMapCount.ShouldBe(1);

        await Should.ThrowAsync<PostCommitCallbackException>(() => session.SaveChangesAsync(cancellation.Token));

        session.IdentityMapCount.ShouldBe(0);
        // An unchanged save after the fault must not repeat the acknowledged write.
        (await session.SaveChangesAsync()).ShouldBe(0);
        await engine.Received(1).Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancellation_before_commit_submission_preserves_cancellation_and_rolls_back(bool explicitTransaction)
    {
        using var cancellation = new CancellationTokenSource();
        var engine = CreateEngine();
        var listener = explicitTransaction ? new CommitObserver() : new CancelBeforeCommitListener(cancellation);
        await using var client = new TransportClient(engine);
        var options = new StoreOptions();
        options.Listeners.Add(listener);
        await using var session = new DocumentSession(client, await client.CreateSession(), options, DocumentTracking.None);
        if (explicitTransaction) await session.BeginTransactionAsync();
        session.Store(new Person { Id = RecordId.From("person", "cancel-before-submit"), Name = "synthetic" });
        if (explicitTransaction)
        {
            await session.SaveChangesAsync();
            cancellation.Cancel();
        }

        var operation = explicitTransaction
            ? session.CommitTransactionAsync(cancellation.Token)
            : session.SaveChangesAsync(cancellation.Token);
        var exception = await Should.ThrowAsync<OperationCanceledException>(() => operation);

        exception.CancellationToken.ShouldBe(cancellation.Token);
        operation.IsCanceled.ShouldBeTrue();
        listener.AfterCommitCalls.ShouldBe(0);
        await engine.DidNotReceive().Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await engine.Received(1).Cancel(Arg.Any<Guid?>(), Arg.Any<Guid>(), CancellationToken.None);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Confirmed_database_rejection_is_not_an_unknown_commit(bool explicitTransaction)
    {
        var engine = CreateEngine();
        var rejection = new ConfirmedCommitRejection();
        engine.Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(rejection));
        var listener = new CommitObserver();
        await using var client = new TransportClient(engine);
        var options = new StoreOptions();
        options.Listeners.Add(listener);
        await using var session = new DocumentSession(client, await client.CreateSession(), options, DocumentTracking.None);
        if (explicitTransaction) await session.BeginTransactionAsync();
        session.Store(new Person { Id = RecordId.From("person", "known-rejection"), Name = "synthetic" });
        if (explicitTransaction) await session.SaveChangesAsync();

        var exception = await Should.ThrowAsync<Exception>(() => explicitTransaction
            ? session.CommitTransactionAsync()
            : session.SaveChangesAsync());

        exception.GetType().Name.ShouldNotBe("CommitOutcomeUnknownException");
        (exception == rejection || exception.InnerException == rejection).ShouldBeTrue();
        listener.AfterCommitCalls.ShouldBe(0);
        await engine.Received(1).Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static ISurrealDbProviderEngine CreateEngine()
    {
        var engine = Substitute.For<ISurrealDbProviderEngine>();
        engine.Version(Arg.Any<CancellationToken>()).Returns(Task.FromResult("3.0.5"));
        engine.CreateSession(Arg.Any<CancellationToken>()).Returns(Task.FromResult(Guid.NewGuid()));
        engine.Begin(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Guid.NewGuid()));
        engine.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));
        return engine;
    }

    [Test]
    [Arguments(false, "io")]
    [Arguments(true, "io")]
    [Arguments(false, "http")]
    [Arguments(true, "http")]
    [Arguments(false, "websocket")]
    [Arguments(true, "websocket")]
    public async Task Lost_transport_at_commit_retains_the_cause_and_never_retries(
        bool explicitTransaction, string transport)
    {
        Exception cause = transport switch
        {
            "io" => new IOException("synthetic transport loss"),
            "http" => new System.Net.Http.HttpRequestException("synthetic transport loss"),
            "websocket" => new System.Net.WebSockets.WebSocketException("synthetic transport loss"),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
        var engine = CreateEngine();
        engine.Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(cause));
        var observer = new CommitObserver();
        await using var client = new TransportClient(engine);
        var options = new StoreOptions();
        options.Listeners.Add(observer);
        await using var session = new DocumentSession(client, await client.CreateSession(), options, DocumentTracking.None);
        if (explicitTransaction) await session.BeginTransactionAsync();
        session.Store(new Person { Id = RecordId.From("person", "transport-loss"), Name = "synthetic" });
        if (explicitTransaction) await session.SaveChangesAsync();

        var exception = await Should.ThrowAsync<CommitOutcomeUnknownException>(() => explicitTransaction
            ? session.CommitTransactionAsync()
            : session.SaveChangesAsync());

        exception.InnerException.ShouldBeSameAs(cause);
        observer.AfterCommitCalls.ShouldBe(0);
        await engine.Received(1).Commit(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await engine.DidNotReceive().Cancel(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private sealed class TransportClient : BaseSurrealDbClient
    {
        public TransportClient(ISurrealDbProviderEngine engine)
        {
            Uri = new Uri("ws://synthetic.invalid/rpc");
            InitializeAndSetProviderEngine(engine, new SurrealDbOptions(Uri.ToString()), null, null, null);
        }
    }

    private class CommitObserver : DocumentSessionListenerBase
    {
        public int AfterCommitCalls { get; protected set; }
        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken token)
        {
            AfterCommitCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingAfterCommitListener : CommitObserver
    {
        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken token)
        {
            AfterCommitCalls++;
            throw new InvalidOperationException("synthetic postcommit observer failure");
        }
    }

    private sealed class ObservedCallbackFailure(Exception cause, CancellationTokenSource? cancellation) : CommitObserver
    {
        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken token)
        {
            AfterCommitCalls++;
            cancellation?.Cancel();
            return Task.FromException(cause);
        }
    }

    private sealed class CancelBeforeCommitListener(CancellationTokenSource cancellation) : CommitObserver
    {
        public override Task BeforeCommitAsync(IDocumentSession session, CancellationToken token)
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class ConfirmedCommitRejection() : SurrealDbRpcException("synthetic confirmed write conflict");
}
