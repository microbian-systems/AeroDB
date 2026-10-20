using AeroDB.Sable;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>Provider-boundary regressions; no live database or real secret is used.</summary>
public sealed class VaultSaveBoundaryRegressionTests
{
    [Test]
    public async Task SaveChangesAsync_preserves_precommit_cancellation_and_its_token()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Substitute.For<ISurrealDbSession>();
        var listener = new CommitObserver();
        provider.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<SurrealDbResponse>(call.ArgAt<CancellationToken>(2));
            });
        await using var session = CreateSession(provider, listener);
        session.Store(new Person { Id = RecordId.From("person", "cancel-before-commit"), Name = "not-committed" });

        var save = session.SaveChangesAsync(cancellation.Token);
        var exception = await Should.ThrowAsync<OperationCanceledException>(() => save);

        exception.CancellationToken.ShouldBe(cancellation.Token);
        save.IsCanceled.ShouldBeTrue();
        listener.AfterCommitCalls.ShouldBe(0);
        await provider.Received(1).RawQuery(Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), cancellation.Token);
    }

    [Test]
    public async Task SaveChangesAsync_preserves_listener_cancellation_without_writing()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = SuccessfulProvider();
        var listener = new CancellingListener(cancellation);
        await using var session = CreateSession(provider, listener);
        session.Store(new Person { Id = RecordId.From("person", "cancel-listener"), Name = "not-written" });

        var exception = await Should.ThrowAsync<OperationCanceledException>(
            () => session.SaveChangesAsync(cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        listener.AfterCommitCalls.ShouldBe(0);
        await provider.DidNotReceive().RawQuery(Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveChangesAsync_keeps_ordinary_provider_failure_distinct_from_cancellation()
    {
        var provider = Substitute.For<ISurrealDbSession>();
        var failure = new InvalidOperationException("synthetic provider failure");
        provider.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SurrealDbResponse>(failure));
        var listener = new CommitObserver();
        await using var session = CreateSession(provider, listener);
        session.Store(new Person { Id = RecordId.From("person", "ordinary-failure"), Name = "not-written" });

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());

        exception.Message.ShouldBe("Failed to save changes.");
        exception.InnerException.ShouldBeSameAs(failure);
        listener.AfterCommitCalls.ShouldBe(0);
    }

    [Test]
    public async Task Debug_write_logging_excludes_document_values_from_rendered_and_structured_records()
    {
        const string canary = "synthetic-vault-document-value-should-not-be-logged";
        const string identityCanary = "synthetic-vault-private-path-should-not-be-logged";
        var logger = new CapturingLogger();
        await using var session = CreateSession(SuccessfulProvider(), new CommitObserver());
        session.Logger = logger;
        session.Store(new Person { Id = RecordId.From("person", identityCanary), Name = canary, Email = "synthetic-audit-path@example.invalid" });

        (await session.SaveChangesAsync()).ShouldBe(1);

        logger.Entries.ShouldNotBeEmpty();
        string.Join("\n", logger.Entries).ShouldNotContain(canary);
        string.Join("\n", logger.Entries).ShouldNotContain(identityCanary);
        string.Join("\n", logger.Entries).ShouldNotContain("synthetic-audit-path@example.invalid");
        logger.Entries.ShouldContain(entry => entry.Contains("committed 1 changes", StringComparison.Ordinal));
    }

    [Test]
    public async Task Failed_provider_logging_excludes_private_identity_and_exception_values()
    {
        const string identityCanary = "synthetic-private-vault-key-path";
        const string exceptionCanary = "synthetic-provider-secret-value";
        var provider = Substitute.For<ISurrealDbSession>();
        provider.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SurrealDbResponse>(new InvalidOperationException(exceptionCanary)));
        var logger = new CapturingLogger();
        var listener = new CommitObserver();
        await using var session = CreateSession(provider, listener);
        session.Logger = logger;
        session.Store(new Person { Id = RecordId.From("person", identityCanary), Name = "synthetic" });

        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());

        listener.AfterCommitCalls.ShouldBe(0);
        var entries = string.Join("\n", logger.Entries);
        entries.ShouldNotContain(identityCanary);
        entries.ShouldNotContain(exceptionCanary);
        logger.Entries.ShouldContain(entry => entry.Contains("SaveChangesAsync failed", StringComparison.Ordinal));
    }

    [Test]
    public async Task Failed_response_logging_excludes_private_identity_and_database_error_values()
    {
        const string identityCanary = "synthetic-private-response-key-path";
        const string responseCanary = "synthetic-database-secret-value";
        var provider = Substitute.For<ISurrealDbSession>();
        provider.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([new SensitiveErrorResult(responseCanary)])));
        var logger = new CapturingLogger();
        var listener = new CommitObserver();
        await using var session = CreateSession(provider, listener);
        session.Logger = logger;
        session.Store(new Person { Id = RecordId.From("person", identityCanary), Name = "synthetic" });

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());

        listener.AfterCommitCalls.ShouldBe(0);
        var entries = string.Join("\n", logger.Entries);
        entries.ShouldNotContain(identityCanary);
        entries.ShouldNotContain(responseCanary);
        exception.ToString().ShouldNotContain(responseCanary);
        exception.ToString().ShouldNotContain(identityCanary);
        logger.Entries.ShouldContain(entry => entry.Contains("SaveChangesAsync failed", StringComparison.Ordinal));
    }

    [Test]
    public async Task Successful_save_still_writes_once_and_notifies_after_commit()
    {
        var provider = SuccessfulProvider();
        var listener = new CommitObserver();
        await using var session = CreateSession(provider, listener);
        session.Store(new Person { Id = RecordId.From("person", "success-control"), Name = "ordinary-document" });

        (await session.SaveChangesAsync()).ShouldBe(1);

        listener.AfterCommitCalls.ShouldBe(1);
        await provider.Received(1).RawQuery(Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    private static DocumentSession CreateSession(ISurrealDbSession provider, DocumentSessionListenerBase listener)
    {
        var options = new StoreOptions();
        options.Listeners.Add(listener);
        return new DocumentSession(Substitute.For<ISurrealDbClient>(), provider, options, DocumentTracking.None);
    }

    private static ISurrealDbSession SuccessfulProvider()
    {
        var provider = Substitute.For<ISurrealDbSession>();
        provider.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));
        return provider;
    }

    private class CommitObserver : DocumentSessionListenerBase
    {
        public int AfterCommitCalls { get; private set; }
        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken token)
        {
            AfterCommitCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class SensitiveErrorResult(string details) : ISurrealDbErrorResult
    {
        public bool IsOk => false;
        public override string ToString() => details;
    }

    private sealed class CancellingListener(CancellationTokenSource cancellation) : CommitObserver
    {
        public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> structured)
                Entries.Add(string.Join(";", structured.Select(field => $"{field.Key}={field.Value}")));
            if (exception is not null) Entries.Add(exception.ToString());
        }
    }
}
