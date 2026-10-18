using AeroDB.Sable;
using System.Buffers;
using Dahomey.Cbor;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using SurrealDb.Net.Exceptions.Response;
using SurrealDb.Net.Internals.Cbor;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Characterizes the public LINQ read boundary. A native response carrying an
/// error is not an empty result, zero aggregate, false existence answer, or a
/// missing first/single value.
/// </summary>
public sealed class LinqReadFailureRegressionTests
{
    private sealed class NativeErrorResult : ISurrealDbErrorResult
    {
        public bool IsOk => false;
    }

    private sealed class NativeOkResult : ISurrealDbResult
    {
        public bool IsOk => true;
    }

    private static SurrealDbResponse ErrorResponse()
        => new(new List<ISurrealDbResult> { new NativeErrorResult() });

    private static SurrealDbResponse EmptyResponse()
        => new(new List<ISurrealDbResult>());

    private static SurrealDbResponse LaterStatementErrorResponse()
        => new(new List<ISurrealDbResult> { new NativeOkResult(), new NativeErrorResult() });

    private static SurrealDbResponse SuccessResponse<T>(T payload)
    {
        var options = SurrealDbCborOptions.GetCborSerializerOptions(null);
        var envelope = new Dictionary<string, object?>
        {
            ["status"] = "OK",
            ["time"] = "1ns",
            ["result"] = payload
        };
        var bytes = new ArrayBufferWriter<byte>();
        Cbor.Serialize(envelope, bytes, options);
        var result = Cbor.Deserialize<ISurrealDbResult>(bytes.WrittenSpan, options);

        return new SurrealDbResponse(new List<ISurrealDbResult> { result });
    }

    private static (QuerySession Session, ISurrealDbSession Driver) SessionFor(SurrealDbResponse response)
        => SessionFor([response]);

    private static (QuerySession Session, ISurrealDbSession Driver) SessionFor(IReadOnlyList<SurrealDbResponse> responses)
    {
        if (responses.Count == 0)
            throw new ArgumentException("At least one synthetic response is required.", nameof(responses));

        var client = Substitute.For<ISurrealDbClient>();
        var driver = Substitute.For<ISurrealDbSession>();
        var nextResponse = 0;
        driver.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(responses[Math.Min(nextResponse++, responses.Count - 1)]));

        return (
            new QuerySession(client, driver, new StoreOptions(), DocumentTracking.None),
            driver);
    }

    private static (QuerySession Session, ISurrealDbSession Driver) SessionForCanceledDispatch(
        CancellationTokenSource cancellation)
    {
        var client = Substitute.For<ISurrealDbClient>();
        var driver = Substitute.For<ISurrealDbSession>();
        driver.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                cancellation.Token)
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<SurrealDbResponse>(cancellation.Token);
            });

        return (
            new QuerySession(client, driver, new StoreOptions(), DocumentTracking.None),
            driver);
    }

    private static Task ExecuteTerminalAsync(QuerySession session, string terminal, CancellationToken ct)
        => terminal switch
        {
            "ToList" => session.Query<Person>().ToListAsync(ct),
            "FirstOrDefault" => session.Query<Person>().FirstOrDefaultAsync(ct),
            "SingleOrDefault" => session.Query<Person>().SingleOrDefaultAsync(ct),
            "Count" => session.Query<Person>().CountAsync(ct),
            "Any" => session.Query<Person>().AnyAsync(ct),
            "Sum" => session.Query<Product>().SumAsync(product => product.Price, ct),
            "Min" => session.Query<Product>().MinAsync(product => product.Price, ct),
            "Max" => session.Query<Product>().MaxAsync(product => product.Price, ct),
            "Average" => session.Query<Product>().AverageAsync(product => product.Price, ct),
            "AggregateBuilder" => session.Query<Product>()
                .AggregateQuery(builder => builder.Sum(product => product.Price))
                .ToListAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(terminal), terminal, "Unknown public LINQ terminal.")
        };

    [Test]
    [Arguments("ToList")]
    [Arguments("FirstOrDefault")]
    [Arguments("SingleOrDefault")]
    [Arguments("Count")]
    [Arguments("Any")]
    [Arguments("Sum")]
    [Arguments("Min")]
    [Arguments("Max")]
    [Arguments("Average")]
    [Arguments("AggregateBuilder")]
    public async Task Public_terminal_apis_preserve_native_read_errors(string terminal)
    {
        var (session, driver) = SessionFor(ErrorResponse());
        await using (session)
        {
            using var cancellation = new CancellationTokenSource();

            await Should.ThrowAsync<ResponseUnsuccessfulException>(
                () => ExecuteTerminalAsync(session, terminal, cancellation.Token));

            await driver.Received(1).RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                cancellation.Token);
        }
    }

    [Test]
    public async Task Statistics_without_includes_preserves_a_native_read_error()
    {
        var (session, _) = SessionFor(ErrorResponse());
        await using (session)
        {
            var query = session.Query<Person>().Stats(out _);

            await Should.ThrowAsync<ResponseUnsuccessfulException>(
                () => query.ToListAsync());
        }
    }

    [Test]
    public async Task Statistics_with_includes_preserves_a_later_statement_error()
    {
        var (session, _) = SessionFor(LaterStatementErrorResponse());
        await using (session)
        {
            var query = session.Query<OrderWithCustomer>()
                .Include(order => order.Customer)
                .Stats(out _);

            await Should.ThrowAsync<ResponseUnsuccessfulException>(
                () => query.ToListAsync());
        }
    }

    [Test]
    public async Task Statistics_with_includes_does_not_swallow_a_count_error_before_the_main_query()
    {
        // Includes deliberately take the separate CountAsync route. A valid
        // empty main response must not erase the preceding count failure.
        var (session, driver) = SessionFor([ErrorResponse(), EmptyResponse()]);
        await using (session)
        {
            var query = session.Query<OrderWithCustomer>()
                .Include(order => order.Customer)
                .Stats(out _);

            await Should.ThrowAsync<ResponseUnsuccessfulException>(
                () => query.ToListAsync());

            await driver.Received(1).RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task Fetch_query_preserves_a_native_read_error_before_hydration()
    {
        var (session, _) = SessionFor(ErrorResponse());
        await using (session)
        {
            var query = session.Query<OrderWithCustomer>().Fetch(order => order.Customer);

            await Should.ThrowAsync<ResponseUnsuccessfulException>(
                () => query.ToListAsync());
        }
    }

    [Test]
    public async Task Valid_empty_typed_query_remains_an_empty_result()
    {
        var (session, _) = SessionFor(EmptyResponse());
        await using (session)
        {
            var rows = await session.Query<Person>().ToListAsync();

            rows.ShouldBeEmpty();
        }
    }

    [Test]
    public async Task Real_cbor_successful_empty_result_preserves_empty_and_default_terminal_semantics()
    {
        var (session, _) = SessionFor(SuccessResponse(Array.Empty<Person>()));
        await using (session)
        {
            var rows = await session.Query<Person>().ToListAsync();
            var first = await session.Query<Person>().FirstOrDefaultAsync();
            var single = await session.Query<Person>().SingleOrDefaultAsync();

            rows.ShouldBeEmpty();
            first.ShouldBeNull();
            single.ShouldBeNull();
        }
    }

    [Test]
    public async Task Real_cbor_successful_result_materializes_the_public_record_fields()
    {
        var expected = new Person
        {
            Name = "Ada Lovelace",
            Age = 36,
            Email = "ada@example.test",
            Tags = ["math", "analysis"]
        };
        var (session, _) = SessionFor(SuccessResponse(new[] { expected }));
        await using (session)
        {
            var rows = await session.Query<Person>().ToListAsync();
            var first = await session.Query<Person>().FirstOrDefaultAsync();
            var single = await session.Query<Person>().SingleOrDefaultAsync();

            rows.Count.ShouldBe(1);
            rows[0].Name.ShouldBe(expected.Name);
            rows[0].Age.ShouldBe(expected.Age);
            rows[0].Email.ShouldBe(expected.Email);
            rows[0].Tags.ShouldBe(expected.Tags);
            first.ShouldNotBeNull();
            first.Name.ShouldBe(expected.Name);
            single.ShouldNotBeNull();
            single.Email.ShouldBe(expected.Email);
        }
    }

    [Test]
    public async Task Statistics_with_includes_preserves_a_later_main_statement_error_after_a_successful_count()
    {
        var count = SuccessResponse(new[] { new Dictionary<string, long> { ["count"] = 1 } });
        var (session, driver) = SessionFor([count, LaterStatementErrorResponse()]);
        await using (session)
        {
            var query = session.Query<OrderWithCustomer>()
                .Include(order => order.Customer)
                .Stats(out var statistics);

            await Should.ThrowAsync<ResponseUnsuccessfulException>(() => query.ToListAsync());

            statistics.TotalResults.ShouldBe(1);
            await driver.Received(2).RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task Statistics_with_includes_preserves_count_cancellation_without_a_main_query_retry()
    {
        using var cancellation = new CancellationTokenSource();
        var (session, driver) = SessionForCanceledDispatch(cancellation);
        await using (session)
        {
            var query = session.Query<OrderWithCustomer>()
                .Include(order => order.Customer)
                .Stats(out _);

            var error = await Should.ThrowAsync<OperationCanceledException>(
                () => query.ToListAsync(cancellation.Token));

            error.CancellationToken.ShouldBe(cancellation.Token);
            await driver.Received(1).RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                cancellation.Token);
        }
    }

    [Test]
    public async Task Caller_cancellation_is_preserved_without_a_retry()
    {
        using var cancellation = new CancellationTokenSource();
        var (session, driver) = SessionForCanceledDispatch(cancellation);
        await using (session)
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(
                () => session.Query<Person>().ToListAsync(cancellation.Token));

            error.CancellationToken.ShouldBe(cancellation.Token);
            await driver.Received(1).RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                cancellation.Token);
        }
    }
}
