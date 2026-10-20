using AeroDB.Sable;
using NSubstitute;
using SurrealDb.Net;
using SurrealDb.Net.Exceptions.Response;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Regression coverage for the fail-closed read boundary required by Aero.Vault.
/// A database error must never be represented as a missing document, an empty
/// query result, or a successful statement.
/// </summary>
public sealed class VaultReadFailureRegressionTests
{
    [Test]
    public async Task RawQueryAsync_throws_when_the_database_reports_an_error()
    {
        await using var session = CreateQuerySessionReturningError();

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => session.RawQueryAsync<Person>("SELECT * FROM person"));
    }

    [Test]
    public async Task RawQueryAsync_throws_when_a_later_statement_reports_an_error()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.QuerySessionAsync();

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => session.RawQueryAsync<Person>(
                "SELECT * FROM person; THROW 'later statement failed';"));
    }

    [Test]
    public async Task ExecuteSqlAsync_throws_when_the_database_reports_an_error()
    {
        await using var session = CreateQuerySessionReturningError();

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => session.ExecuteSqlAsync("CREATE person CONTENT { name: 'ignored' }"));
    }

    [Test]
    public async Task LoadAsync_throws_when_the_database_reports_an_error()
    {
        await using var session = CreateQuerySessionReturningError();

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => session.LoadAsync<Person>("cannot-be-read"));
    }

    [Test]
    public async Task LoadAsync_preserves_provider_cancellation_from_the_requested_caller_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var databaseSession = Substitute.For<ISurrealDbSession>();
        databaseSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Is<CancellationToken>(token => token == cancellation.Token))
            .Returns(Task.FromCanceled<SurrealDbResponse>(cancellation.Token));

        await using var session = new QuerySession(
            Substitute.For<ISurrealDbClient>(),
            databaseSession,
            new StoreOptions(),
            DocumentTracking.None);

        var task = session.LoadAsync<Person>("cancelled-read", cancellation.Token);

        var exception = await Should.ThrowAsync<OperationCanceledException>(() => task);

        exception.CancellationToken.ShouldBe(cancellation.Token);
        task.IsCanceled.ShouldBeTrue();
    }

    [Test]
    public async Task CheckExistsAsync_throws_when_the_database_reports_an_error()
    {
        await using var session = CreateQuerySessionReturningError();

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => session.CheckExistsAsync<Person>("cannot-be-read"));
    }

    [Test]
    public async Task RawQueryAsync_returns_an_empty_result_for_a_valid_absent_match()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var writeSession = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        writeSession.Store(new Person { Name = "present", Age = 30 });
        await writeSession.SaveChangesAsync();

        await using var querySession = await store.QuerySessionAsync();
        var result = await querySession.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE name = 'absent'");

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_returns_null_for_a_valid_missing_document()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var writeSession = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        writeSession.Store(new Person { Name = "existing-document", Age = 30 });
        await writeSession.SaveChangesAsync();
        await using var session = await store.QuerySessionAsync();

        var result = await session.LoadAsync<Person>("missing-document");

        result.ShouldBeNull();
    }

    [Test]
    public async Task RawQueryAsync_returns_records_when_the_database_reports_success()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var writeSession = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        writeSession.Store(new Person { Name = "successful-read", Age = 30 });
        await writeSession.SaveChangesAsync();

        await using var querySession = await store.QuerySessionAsync();
        var result = await querySession.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE name = 'successful-read'");

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("successful-read");
    }

    private static QuerySession CreateQuerySessionReturningError()
    {
        var databaseSession = Substitute.For<ISurrealDbSession>();
        databaseSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([new ErrorResult()])));

        return new QuerySession(
            Substitute.For<ISurrealDbClient>(),
            databaseSession,
            new StoreOptions(),
            DocumentTracking.None);
    }

    private sealed class ErrorResult : ISurrealDbErrorResult
    {
        public bool IsOk => false;
    }
}
