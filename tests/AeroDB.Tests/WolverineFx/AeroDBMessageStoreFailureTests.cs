using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using SurrealDb.Net.Exceptions.Response;
using SurrealDb.Net.Models.Response;
using TUnit.Core;
using Wolverine;

namespace AeroDB.Tests;

public sealed class AeroDBMessageStoreFailureTests
{
    [Test]
    public async Task InitializeSchemaAsync_fails_closed_before_creating_an_anonymous_session_when_credentials_are_configured()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = new AeroDBMessageStore(
            client,
            NullLogger<AeroDBMessageStore>.Instance,
            storeOptions: new StoreOptions
            {
                Namespace = "secured",
                Database = "durability",
                Username = "root-user",
                Password = "root-password"
            });

        await Should.ThrowAsync<InvalidOperationException>(() => store.InitializeSchemaAsync());

        await client.DidNotReceive().CreateSession(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoadOutgoingAsync_throws_when_the_database_reports_an_error()
    {
        var client = CreateClientReturning(new SurrealDbResponse([new ErrorResult()]));
        var store = CreateStore(client);

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.LoadOutgoingAsync(new Uri("loopback://outgoing")));
    }

    [Test]
    public async Task LoadPageOfGloballyOwnedIncomingAsync_filters_by_the_requested_listener_and_throws_for_an_error()
    {
        var listener = new Uri("loopback://listener");
        var client = Substitute.For<ISurrealDbClient>();
        client.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([new ErrorResult()])));
        var store = CreateStore(client);

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.LoadPageOfGloballyOwnedIncomingAsync(listener, 10));

        await client.Received(1).RawQuery(
            Arg.Is<string>(query => query.Contains("received_at = $p0", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>>(parameters =>
                Equals(parameters["p0"], listener.ToString())),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoadOutgoingAsync_throws_when_the_response_has_no_typed_result()
    {
        var client = CreateClientReturning(new SurrealDbResponse([]));
        var store = CreateStore(client);

        await Should.ThrowAsync<IndexOutOfRangeException>(
            () => store.LoadOutgoingAsync(new Uri("loopback://outgoing")));
    }

    [Test]
    public async Task Durable_mutations_throw_when_the_database_reports_an_error()
    {
        var client = CreateClientReturning(new SurrealDbResponse([new ErrorResult()]));
        var store = CreateStore(client);
        var envelope = CreateEnvelope();

        await Should.ThrowAsync<ResponseUnsuccessfulException>(() => store.ReassignIncomingAsync(7, [envelope]));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(() => store.DeleteOutgoingAsync([envelope]));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(() => store.DeleteOutgoingAsync(envelope));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.DiscardAndReassignOutgoingAsync([envelope], [envelope], 7));
    }

    [Test]
    public async Task Recovery_metadata_fails_closed_when_any_database_response_is_unsuccessful()
    {
        var client = CreateClientReturning(new SurrealDbResponse([new ErrorResult(), new ErrorResult()]));
        var store = CreateStore(client);

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.FindGloballyOwnedIncomingCountsAsync(CancellationToken.None));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.FindGloballyOwnedOutgoingDestinationsAsync(CancellationToken.None));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.FindDistinctOwnerIdsAsync(CancellationToken.None));
    }

    [Test]
    public async Task LoadAllNodesAsync_preserves_the_original_cancelled_token_without_querying_authority()
    {
        var client = CreateClientReturning(new SurrealDbResponse([]));
        var store = CreateStore(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Should.ThrowAsync<OperationCanceledException>(
            () => store.LoadAllNodesAsync(cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        await client.DidNotReceive().RawQuery(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Ownership_release_fails_closed_when_any_database_response_is_unsuccessful()
    {
        var client = CreateClientReturning(new SurrealDbResponse([new ErrorResult(), new ErrorResult()]));
        var store = CreateStore(client);

        await Should.ThrowAsync<ResponseUnsuccessfulException>(() => store.ReleaseAllOwnershipAsync());
        await Should.ThrowAsync<ResponseUnsuccessfulException>(() => store.ReleaseAllOwnershipAsync(41));
    }

    [Test]
    public async Task Owner_specific_release_preserves_the_original_cancelled_token_without_querying()
    {
        var client = CreateClientReturning(new SurrealDbResponse([]));
        var store = CreateStore(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Should.ThrowAsync<OperationCanceledException>(
            () => store.ReleaseAllOwnershipAsync(41, cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        await client.DidNotReceive().RawQuery(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Owner_specific_release_cancels_an_in_flight_database_command_with_the_original_token()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<ISurrealDbClient>();
        client.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => WaitForCancellationAsync(
                call.ArgAt<CancellationToken>(2),
                entered));
        var store = CreateStore(client);
        using var cancellation = new CancellationTokenSource();

        var release = store.ReleaseAllOwnershipAsync(41, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var exception = await Should.ThrowAsync<OperationCanceledException>(() => release);
        exception.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Test]
    public async Task Inbox_mutations_and_existence_checks_throw_when_the_database_reports_an_error()
    {
        var client = CreateClientReturning(new SurrealDbResponse([new ErrorResult()]));
        var store = CreateStore(client);
        var envelope = CreateEnvelope();

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.RescheduleExistingEnvelopeForRetryAsync(envelope));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.ScheduleExecutionAsync(envelope));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.MoveToDeadLetterStorageAsync(envelope, null));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.IncrementIncomingEnvelopeAttemptsAsync(envelope));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.MarkIncomingEnvelopeAsHandledAsync([envelope]));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.ReleaseIncomingAsync(7, envelope.Destination!));
        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.ExistsAsync(envelope, CancellationToken.None));
    }

    [Test]
    public async Task ExistsAsync_honors_cancellation_and_rejects_a_disposed_store()
    {
        var client = CreateClientReturning(new SurrealDbResponse([]));
        var store = CreateStore(client);
        var envelope = CreateEnvelope();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => store.ExistsAsync(envelope, cancellation.Token));
        await client.DidNotReceive().RawQuery(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());

        await store.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(
            () => store.ExistsAsync(envelope, CancellationToken.None));
    }

    [Test]
    public async Task Batch_handling_uses_one_transactional_database_command()
    {
        var client = CreateClientReturning(new SurrealDbResponse([new ErrorResult()]));
        var store = CreateStore(client);
        var first = CreateEnvelope();
        var second = CreateEnvelope();

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => store.MarkIncomingEnvelopeAsHandledAsync([first, second]));

        await client.Received(1).RawQuery(
            Arg.Is<string>(query =>
                query.StartsWith("BEGIN TRANSACTION;", StringComparison.Ordinal) &&
                query.Contains("$updated0", StringComparison.Ordinal) &&
                query.Contains("$updated1", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>>(parameters =>
                parameters.ContainsKey("id0") && parameters.ContainsKey("id1") &&
                parameters.ContainsKey("keepUntil0") && parameters.ContainsKey("keepUntil1")),
            Arg.Any<CancellationToken>());
    }

    private static AeroDBMessageStore CreateStore(ISurrealDbClient client) =>
        new(client, NullLogger<AeroDBMessageStore>.Instance);

    private static ISurrealDbClient CreateClientReturning(SurrealDbResponse response)
    {
        var client = Substitute.For<ISurrealDbClient>();
        client.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));
        return client;
    }

    private static Envelope CreateEnvelope() => new()
    {
        Id = Guid.NewGuid(),
        Destination = new Uri("loopback://outgoing"),
        MessageType = "tests.message-store",
        Data = "message-store"u8.ToArray()
    };

    private static async Task<SurrealDbResponse> WaitForCancellationAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource entered)
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The cancellation test command unexpectedly completed.");
    }

    private sealed class ErrorResult : ISurrealDbErrorResult
    {
        public bool IsOk => false;
    }
}
