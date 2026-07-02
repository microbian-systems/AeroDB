using System.Reflection;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using SurrealDb.Net.Models.Errors;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace Dali.Tests;

public class BatchedQueryTests
{
    // ─── Helpers ─────────────────────────────────────────────────────

    private static async Task<IDocumentStore> CreateStoreAsync()
        => await TestHarness.CreateStoreAsync();

    private static async Task SeedPeople(IDocumentSession session)
    {
        session.Store(new Person { Name = "Alice", Age = 30, Email = "alice@test.com" });
        session.Store(new Person { Name = "Bob", Age = 25, Email = "bob@test.com" });
        session.Store(new Person { Name = "Charlie", Age = 35, Email = "charlie@test.com" });
        session.Store(new Person { Name = "Diana", Age = 22, Email = "diana@test.com" });
        await session.SaveChangesAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  INTEGRATION TESTS (existing, preserved)
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Batch_query_returns_results_for_single_compiled_query()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();
        var alice = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });

        await batch.Execute();

        var result = await alice;
        result.ShouldNotBeNull();
        result.Name.ShouldBe("Alice");
        result.Age.ShouldBe(30);
    }

    [Test]
    public async Task Batch_query_returns_results_for_multiple_compiled_queries()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();
        var alice = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        var olderThan25 = batch.Query(new ListPeopleOlderThan { MinAge = 25 });
        var bob = batch.Query(new FindPersonByFirstName { FirstName = "Bob" });

        await batch.Execute();

        // Single-result query
        var aliceResult = await alice;
        aliceResult.ShouldNotBeNull();
        aliceResult.Name.ShouldBe("Alice");

        // List query
        var olderList = (await olderThan25).ToList();
        olderList.Count.ShouldBe(2); // Alice(30) and Charlie(35)
        olderList.ShouldAllBe(p => p.Age > 25);

        // Another single-result
        var bobResult = await bob;
        bobResult.ShouldNotBeNull();
        bobResult.Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Batch_query_with_different_parameter_values()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();
        var alice = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        var charlie = batch.Query(new FindPersonByFirstName { FirstName = "Charlie" });

        await batch.Execute();

        var aliceResult = await alice;
        aliceResult.ShouldNotBeNull();
        aliceResult.Name.ShouldBe("Alice");

        var charlieResult = await charlie;
        charlieResult.ShouldNotBeNull();
        charlieResult.Name.ShouldBe("Charlie");
    }

    [Test]
    public async Task Batch_query_empty_returns_empty()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var batch = session.CreateBatchQuery();

        // Should not throw — no queries to execute
        await batch.Execute();
    }

    [Test]
    public async Task Batch_query_multiple_execute_calls()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();

        // First batch: Alice
        var alice = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        await batch.Execute();

        var aliceResult = await alice;
        aliceResult.ShouldNotBeNull();
        aliceResult.Name.ShouldBe("Alice");

        // Second batch: Bob (plus Alice again, since items are additive)
        var bob = batch.Query(new FindPersonByFirstName { FirstName = "Bob" });
        await batch.Execute();

        // Alice should still have her result (already resolved)
        aliceResult = await alice;
        aliceResult.ShouldNotBeNull();
        aliceResult.Name.ShouldBe("Alice");

        // Bob should now have his result
        var bobResult = await bob;
        bobResult.ShouldNotBeNull();
        bobResult.Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Batch_query_futures_preserve_order()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();

        // Add queries in a specific order
        var first = batch.Query(new FindPersonByFirstName { FirstName = "Charlie" });
        var second = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        var third = batch.Query(new FindPersonByFirstName { FirstName = "Bob" });

        await batch.Execute();

        // Futures should preserve the order they were added
        var firstResult = await first;
        firstResult.ShouldNotBeNull();
        firstResult.Name.ShouldBe("Charlie");

        var secondResult = await second;
        secondResult.ShouldNotBeNull();
        secondResult.Name.ShouldBe("Alice");

        var thirdResult = await third;
        thirdResult.ShouldNotBeNull();
        thirdResult.Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Batch_query_mixed_single_and_list_queries()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();
        var single = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        var list = batch.Query(new ListPeopleOlderThan { MinAge = 20 });

        await batch.Execute();

        // Single result
        var singleResult = await single;
        singleResult.ShouldNotBeNull();
        singleResult.Name.ShouldBe("Alice");

        // List result
        var listResult = (await list).ToList();
        listResult.Count.ShouldBe(4);
        listResult.ShouldAllBe(p => p.Age > 20);
    }

    [Test]
    public async Task Batch_query_with_skip_take()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Seed 5 people with ordered names
        session.Store(new Person { Name = "Eve", Age = 28 });
        session.Store(new Person { Name = "Frank", Age = 32 });
        session.Store(new Person { Name = "Grace", Age = 27 });
        session.Store(new Person { Name = "Hank", Age = 35 });
        session.Store(new Person { Name = "Ivy", Age = 29 });
        await session.SaveChangesAsync();

        var batch = session.CreateBatchQuery();

        // Query with Skip/Take and a simple list query together
        var paged = batch.Query(new PagedPeopleQuery { Skip = 1, Limit = 2 });
        var all = batch.Query(new ListPeopleOlderThan { MinAge = 20 });

        await batch.Execute();

        // Paged results: skip 1, take 2 → Frank, Grace
        var pagedList = (await paged).ToList();
        pagedList.Count.ShouldBe(2);
        pagedList[0].Name.ShouldBe("Frank");
        pagedList[1].Name.ShouldBe("Grace");

        // All results
        var allList = (await all).ToList();
        allList.Count.ShouldBe(5);
    }

    [Test]
    public async Task Batch_query_string_contains()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();
        var containsAli = batch.Query(new ListPeopleByNameContains { Search = "Ali" });
        var containsChar = batch.Query(new ListPeopleByNameContains { Search = "Char" });

        await batch.Execute();

        var aliResults = (await containsAli).ToList();
        aliResults.Count.ShouldBe(1);
        aliResults[0].Name.ShouldBe("Alice");

        var charResults = (await containsChar).ToList();
        charResults.Count.ShouldBe(1);
        charResults[0].Name.ShouldBe("Charlie");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UNIT TESTS — Constructor
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Constructor_creates_instance_with_valid_session()
    {
        var (session, _) = CreateMockSession();
        var batchQuery = new BatchedQuery(session);
        batchQuery.ShouldNotBeNull();
        batchQuery.ShouldBeOfType<BatchedQuery>();
        batchQuery.ShouldBeAssignableTo<IBatchedQuery>();
    }

    [Test]
    public void Constructor_throws_ArgumentNullException_when_session_is_null()
    {
        Should.Throw<ArgumentNullException>(() => new BatchedQuery(null!));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UNIT TESTS — FormatInlineValue (via reflection)
    // ═══════════════════════════════════════════════════════════════════

    private static string InvokeFormatInlineValue(object? value)
    {
        var method = typeof(BatchedQuery).GetMethod(
            "FormatInlineValue",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            [typeof(object)],
            null)!;
        return (string)method.Invoke(null, [value])!;
    }

    [Test]
    public void FormatInlineValue_null_returns_NONE()
    {
        InvokeFormatInlineValue(null).ShouldBe("NONE");
    }

    [Test]
    public void FormatInlineValue_string_returns_quoted()
    {
        InvokeFormatInlineValue("hello").ShouldBe("'hello'");
    }

    [Test]
    public void FormatInlineValue_string_with_single_quote_escapes()
    {
        InvokeFormatInlineValue("it's").ShouldBe("'it\\'s'");
    }

    [Test]
    public void FormatInlineValue_bool_true()
    {
        InvokeFormatInlineValue(true).ShouldBe("true");
    }

    [Test]
    public void FormatInlineValue_bool_false()
    {
        InvokeFormatInlineValue(false).ShouldBe("false");
    }

    [Test]
    public void FormatInlineValue_int()
    {
        InvokeFormatInlineValue(42).ShouldBe("42");
    }

    [Test]
    public void FormatInlineValue_long()
    {
        InvokeFormatInlineValue(42L).ShouldBe("42");
    }

    [Test]
    public void FormatInlineValue_short()
    {
        InvokeFormatInlineValue((short)42).ShouldBe("42");
    }

    [Test]
    public void FormatInlineValue_byte()
    {
        InvokeFormatInlineValue((byte)42).ShouldBe("42");
    }

    [Test]
    public void FormatInlineValue_float()
    {
        InvokeFormatInlineValue(3.14f).ShouldBe("3.14");
    }

    [Test]
    public void FormatInlineValue_double()
    {
        InvokeFormatInlineValue(2.71828).ShouldBe("2.71828");
    }

    [Test]
    public void FormatInlineValue_decimal()
    {
        InvokeFormatInlineValue(1.5m).ShouldBe("1.5");
    }

    [Test]
    public void FormatInlineValue_DateTime()
    {
        var dt = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        InvokeFormatInlineValue(dt).ShouldBe("d'2024-01-15T10:30:00Z'");
    }

    [Test]
    public void FormatInlineValue_DateTimeOffset()
    {
        var dto = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);
        InvokeFormatInlineValue(dto).ShouldBe("d'2024-01-15T10:30:00Z'");
    }

    [Test]
    public void FormatInlineValue_object_fallback()
    {
        var obj = new { Value = 123 };
        InvokeFormatInlineValue(obj).ShouldBe($"'{obj}'");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UNIT TESTS — InlineParameters (via reflection)
    // ═══════════════════════════════════════════════════════════════════

    private static string InvokeInlineParameters(
        string surql, IReadOnlyDictionary<string, object?> parameters)
    {
        var method = typeof(BatchedQuery).GetMethod(
            "InlineParameters",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            [typeof(string), typeof(IReadOnlyDictionary<string, object?>)],
            null)!;
        return (string)method.Invoke(null, [surql, parameters])!;
    }

    [Test]
    public void InlineParameters_null_params_returns_input()
    {
        const string sql = "SELECT * FROM person WHERE Name = $p0";
        InvokeInlineParameters(sql, null!).ShouldBe(sql);
    }

    [Test]
    public void InlineParameters_empty_params_returns_input()
    {
        const string sql = "SELECT * FROM person WHERE Name = $p0";
        InvokeInlineParameters(sql, new Dictionary<string, object?>())
            .ShouldBe(sql);
    }

    [Test]
    public void InlineParameters_one_param_replaces_placeholder()
    {
        const string sql = "SELECT * FROM person WHERE Name = $p0";
        var result = InvokeInlineParameters(sql, new Dictionary<string, object?>
        {
            ["p0"] = "Alice"
        });
        result.ShouldBe("SELECT * FROM person WHERE Name = 'Alice'");
    }

    [Test]
    public void InlineParameters_multiple_params_all_replaced()
    {
        const string sql = "SELECT * FROM person WHERE Name > $p0 AND Age < $p1";
        var result = InvokeInlineParameters(sql, new Dictionary<string, object?>
        {
            ["p0"] = "Alice",
            ["p1"] = 50
        });
        result.ShouldBe("SELECT * FROM person WHERE Name > 'Alice' AND Age < 50");
    }

    [Test]
    public void InlineParameters_param_key_not_in_surql_no_change()
    {
        const string sql = "SELECT * FROM person WHERE Name = $p0";
        var result = InvokeInlineParameters(sql, new Dictionary<string, object?>
        {
            ["p0"] = "Alice",
            ["unused"] = 99
        });
        // $unused does not appear in the SQL so it should not be modified
        result.ShouldBe("SELECT * FROM person WHERE Name = 'Alice'");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UNIT TESTS — BuildSurrealQueryResult (via reflection)
    // ═══════════════════════════════════════════════════════════════════

    private static Type GetBatchItemType()
        => typeof(BatchedQuery).GetNestedType("BatchItem", BindingFlags.NonPublic)!;

    private static object CreateBatchItem(
        object compiledQuery,
        CompiledPlan plan,
        Action<SurrealDbResponse, int> setResult)
    {
        var type = GetBatchItemType();
        var ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public)[0];
        return ctor.Invoke([compiledQuery, plan, setResult]);
    }

    private static SurrealQueryResult InvokeBuildSurrealQueryResult(object batchItem)
    {
        var method = typeof(BatchedQuery).GetMethod(
            "BuildSurrealQueryResult",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            [GetBatchItemType()],
            null)!;
        return (SurrealQueryResult)method.Invoke(null, [batchItem])!;
    }

    [Test]
    public void BuildSurrealQueryResult_applies_limit_from_property()
    {
        var query = new PagedPeopleQuery { Skip = 5, Limit = 10 };
        var plan = CompiledQueryPlanner.GetOrBuildPlan<Person, IEnumerable<Person>>(
            new PagedPeopleQuery { Skip = 0, Limit = 1 });

        var item = CreateBatchItem(query, plan, (_, _) => { });
        var result = InvokeBuildSurrealQueryResult(item);

        result.Limit.ShouldBe(10);
        result.Skip.ShouldBe(5);
        // Calling ToSurrealQL should include the START and LIMIT clauses
        result.ToSurrealQL().ShouldContain("LIMIT 10");
        result.ToSurrealQL().ShouldContain("START 5");
    }

    [Test]
    public void BuildSurrealQueryResult_single_result_overrides_limit_to_1()
    {
        var query = new FindPersonByFirstName { FirstName = "Alice" };
        var plan = CompiledQueryPlanner.GetOrBuildPlan<Person, Person>(query);

        var item = CreateBatchItem(query, plan, (_, _) => { });
        var result = InvokeBuildSurrealQueryResult(item);

        // IsSingleResult queries always get LIMIT 1
        result.Limit.ShouldBe(1);
        result.ToSurrealQL().ShouldContain("LIMIT 1");
    }

    [Test]
    public void BuildSurrealQueryResult_populates_parameters()
    {
        var query = new FindPersonByFirstName { FirstName = "Alice" };
        var plan = CompiledQueryPlanner.GetOrBuildPlan<Person, Person>(query);

        var item = CreateBatchItem(query, plan, (_, _) => { });
        var result = InvokeBuildSurrealQueryResult(item);

        result.Parameters.ShouldNotBeNull();
        result.Parameters.ShouldContainKey("p0");
        result.Parameters["p0"].ShouldBe("Alice");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UNIT TESTS — QueryRawAsync (mock ISurrealDbSession)
    // ═══════════════════════════════════════════════════════════════════

    private static (InternalSessionBase Session, ISurrealDbSession MockDbSession)
        CreateMockSession()
    {
        var mockClient = Substitute.For<ISurrealDbClient>();
        var mockDbSession = Substitute.For<ISurrealDbSession>();
        var options = new StoreOptions();

        // Create a concrete test session; InternalSessionBase is abstract
        var session = new TestInternalSession(mockClient, mockDbSession, options);
        return (session, mockDbSession);
    }

    private sealed class TestInternalSession : InternalSessionBase
    {
        public TestInternalSession(
            ISurrealDbClient client,
            ISurrealDbSession session,
            StoreOptions options)
            : base(client, session, options, DocumentTracking.IdentityOnly)
        {
        }
    }

    [Test]
    public async Task QueryRawAsync_empty_response_returns_empty_list()
    {
        var (session, mockDbSession) = CreateMockSession();
        mockDbSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse(new List<ISurrealDbResult>())));

        var batch = new BatchedQuery(session);
        var results = await batch.QueryRawAsync("SELECT * FROM person");

        results.ShouldBeEmpty();
    }

    [Test]
    public async Task QueryRawAsync_error_result_throws_NotSupportedException()
    {
        var (session, mockDbSession) = CreateMockSession();

        var errorResult = CreateErrorResult(
            RpcErrorKind.Query, TimeSpan.Zero, "ERR", "Bad query");
        var response = new SurrealDbResponse(new List<ISurrealDbResult> { errorResult });
        mockDbSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var batch = new BatchedQuery(session);

        // GetValue<List<object>>(0) throws NotSupportedException when result is an error
        await Should.ThrowAsync<NotSupportedException>(() =>
            batch.QueryRawAsync("SELECT * FROM invalid"));
    }

    /// <summary>
    /// Creates a SurrealDbErrorResult via reflection (constructor is internal to SurrealDb.Net).
    /// </summary>
    private static SurrealDbErrorResult CreateErrorResult(
        RpcErrorKind kind, TimeSpan time, string status, string details)
    {
        var ctor = typeof(SurrealDbErrorResult)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        return (SurrealDbErrorResult)ctor.Invoke([kind, time, status, details]);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UNIT TESTS — Execute (mock ISurrealDbSession)
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Execute_with_no_items_is_no_op()
    {
        var (session, mockDbSession) = CreateMockSession();
        var batch = new BatchedQuery(session);

        // Should not throw and RawQuery should not be called
        await batch.Execute();

        await mockDbSession.DidNotReceive()
            .RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_with_one_item_calls_raw_query()
    {
        var (session, mockDbSession) = CreateMockSession();

        // Return an empty response (the future will get IndexOutOfRangeException,
        // but we only verify the SQL plumbing here)
        mockDbSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse(new List<ISurrealDbResult>())));

        var batch = new BatchedQuery(session);
        var future = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });

        await batch.Execute();

        // Verify RawQuery was called once with SQL containing our compiled query
        await mockDbSession.Received(1).RawQuery(
            Arg.Is<string>(sql => sql.Contains("SELECT") && sql.Contains("`person`")),
            null,
            Arg.Any<CancellationToken>());

        // The future should have an IndexOutOfRangeException (empty mock response)
        var ex = await Should.ThrowAsync<IndexOutOfRangeException>(() => future);
        ex.Message.ShouldContain("out of range");
    }

    [Test]
    public async Task Execute_with_multiple_items_combines_sql()
    {
        var (session, mockDbSession) = CreateMockSession();

        mockDbSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse(new List<ISurrealDbResult>())));

        var batch = new BatchedQuery(session);
        var alice = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        var bob = batch.Query(new FindPersonByFirstName { FirstName = "Bob" });

        await batch.Execute();

        // The combined SQL should contain both queries separated by newline
        await mockDbSession.Received(1).RawQuery(
            Arg.Is<string>(sql => sql.Contains("'Alice'") && sql.Contains("'Bob'")),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_with_error_result_sets_exception_on_future()
    {
        var (session, mockDbSession) = CreateMockSession();

        var errorResult = CreateErrorResult(
            RpcErrorKind.Query, TimeSpan.Zero, "ERR", "Table 'person' does not exist");
        var response = new SurrealDbResponse(new List<ISurrealDbResult> { errorResult });

        mockDbSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var batch = new BatchedQuery(session);
        var future = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });

        await batch.Execute();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => future);
        ex.Message.ShouldContain("Table 'person' does not exist");
    }

    [Test]
    public async Task Execute_sends_combined_surql()
    {
        var (session, mockDbSession) = CreateMockSession();

        var capturedSql = "";
        mockDbSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse(new List<ISurrealDbResult>())))
            .AndDoes(call => capturedSql = call.ArgAt<string>(0));

        var batch = new BatchedQuery(session);
        batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        batch.Query(new ListPeopleOlderThan { MinAge = 25 });

        await batch.Execute();

        // Combined SQL should contain both queries separated by newline
        capturedSql.ShouldNotBeNullOrEmpty();
        capturedSql.ShouldContain("SELECT");
        capturedSql.ShouldContain("`person`");
        capturedSql.ShouldContain("'Alice'");
        capturedSql.ShouldContain("25");
        // Each statement should end with semicolon
        var statements = capturedSql.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        statements.Length.ShouldBe(2);
        statements[0].TrimEnd().ShouldEndWith(";");
        statements[1].TrimEnd().ShouldEndWith(";");
    }

    [Test]
    public async Task Execute_applies_limit_1_for_single_result_queries()
    {
        var (session, mockDbSession) = CreateMockSession();

        var capturedSql = "";
        mockDbSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse(new List<ISurrealDbResult>())))
            .AndDoes(call => capturedSql = call.ArgAt<string>(0));

        var batch = new BatchedQuery(session);
        batch.Query(new FindPersonByFirstName { FirstName = "Alice" }); // IsSingleResult

        await batch.Execute();

        // Single-result queries should have LIMIT 1
        capturedSql.ShouldContain("LIMIT 1");
    }

    [Test]
    public async Task Execute_applies_skip_and_limit_for_paged_queries()
    {
        var (session, mockDbSession) = CreateMockSession();

        var capturedSql = "";
        mockDbSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse(new List<ISurrealDbResult>())))
            .AndDoes(call => capturedSql = call.ArgAt<string>(0));

        var batch = new BatchedQuery(session);
        batch.Query(new PagedPeopleQuery { Skip = 3, Limit = 5 });

        await batch.Execute();

        capturedSql.ShouldContain("START 3");
        capturedSql.ShouldContain("LIMIT 5");
    }

    [Test]
    public async Task Execute_processes_multiple_items_in_order()
    {
        var (session, mockDbSession) = CreateMockSession();

        mockDbSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse(new List<ISurrealDbResult>())));

        var batch = new BatchedQuery(session);
        var future1 = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        var future2 = batch.Query(new FindPersonByFirstName { FirstName = "Bob" });

        // Both should fail with IndexOutOfRangeException (empty mock response),
        // but Execute should complete without throwing
        await batch.Execute();

        var ex1 = await Should.ThrowAsync<IndexOutOfRangeException>(() => future1);
        var ex2 = await Should.ThrowAsync<IndexOutOfRangeException>(() => future2);

        ex1.Message.ShouldContain("0");
        ex2.Message.ShouldContain("1");
    }

    [Test]
    public async Task Execute_reuses_items_across_multiple_calls()
    {
        var (session, mockDbSession) = CreateMockSession();

        var callCount = 0;
        mockDbSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse(new List<ISurrealDbResult>())))
            .AndDoes(_ => callCount++);

        var batch = new BatchedQuery(session);
        var future = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });

        await batch.Execute();
        callCount.ShouldBe(1);

        // Add another item and execute again
        batch.Query(new FindPersonByFirstName { FirstName = "Bob" });
        await batch.Execute();
        callCount.ShouldBe(2);

        // First item's future should still fail with IndexOutOfRangeException
        // (it was already completed with an exception from the first Execute)
        await Should.ThrowAsync<IndexOutOfRangeException>(() => future);
    }
}
