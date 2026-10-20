using AeroDB.Sable;
using AeroDB.WolverineFx;
using SurrealDb.Net.Models.Response;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SurrealDb.Net.Models.Errors;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Disposable-fixture diagnostic for SurrealQL allocation failures. It exists
/// only to retain server error text in a RED receipt; production code never
/// emits database response contents.
/// </summary>
[Category("ServerIntegration")]
[NotInParallel]
public sealed class AeroDBNodeAllocationSqlDiagnosticTests
{
    [Test]
    public async Task Concurrent_raw_allocation_only_reports_confirmed_retryable_write_conflicts(CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = new AeroDBMessageStore(
            documentStore.Client,
            NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null,
            storeOptions: documentStore.Options,
            ownsClient: false);
        await store.InitializeSchemaAsync();

        var nodeId = Guid.NewGuid().ToString();
        var nodeContent = new Dictionary<string, object?>
        {
            ["description"] = "synthetic allocation diagnostic",
            ["capabilities"] = Array.Empty<string>(),
            ["assigned_agents"] = Array.Empty<string>(),
            ["health_check_time"] = DateTimeOffset.UtcNow,
            ["started"] = DateTimeOffset.UtcNow
        };
        var sql = $"BEGIN TRANSACTION; " +
                  $"LET $existing = (SELECT * FROM wolverine_nodes:`{nodeId}`)[0]; " +
                  "IF $existing = NONE { " +
                  "LET $numbers = SELECT VALUE node_number FROM wolverine_nodes WHERE node_number > 0; " +
                  "LET $max = IF array::len($numbers) = 0 THEN NONE ELSE math::max($numbers) END; " +
                  "UPSERT wolverine_node_sequence:global SET next_node_number = IF next_node_number = NONE THEN (IF $max = NONE THEN 1 ELSE $max + 1 END) ELSE next_node_number END; " +
                  "LET $current = (SELECT * FROM wolverine_node_sequence:global)[0]; " +
                  "IF $max != NONE AND $current.next_node_number <= $max { THROW 'Invalid Wolverine node sequence state'; }; " +
                  "LET $sequence = (UPDATE wolverine_node_sequence:global SET next_node_number += 1 RETURN BEFORE)[0]; " +
                  "LET $allocated = $sequence.next_node_number; " +
                  "CREATE wolverine_nodes:`" + nodeId + "` CONTENT object::extend($nodeContent, { node_number: $allocated }); " +
                  "}; COMMIT TRANSACTION;";

        await using var peerStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        var secondSql = sql.Replace(nodeId, Guid.NewGuid().ToString(), StringComparison.Ordinal);
        var responses = await Task.WhenAll(
            documentStore.Client.RawQuery(sql,
                new Dictionary<string, object?> { ["nodeContent"] = nodeContent }, cancellationToken),
            peerStore.Client.RawQuery(secondSql,
                new Dictionary<string, object?> { ["nodeContent"] = nodeContent }, cancellationToken));
        foreach (var response in responses)
        {
            if (!response.HasErrors)
            {
                response.EnsureAllOks();
                continue;
            }

            // These are raw transactions, with no adapter retry policy. Only a
            // server-acknowledged write-conflict rollback is legitimate here.
            var responseErrors = response.Errors.OfType<SurrealDbErrorResult>().ToArray();
            responseErrors.Length.ShouldBe(response.Errors.Count());
            responseErrors.ShouldAllBe(error => error.Kind == RpcErrorKind.Query);
            responseErrors.ShouldContain(error => error.Details ==
                "Cannot COMMIT: Transaction conflict: Write conflict, retry the transaction. This transaction can be retried");
            responseErrors.ShouldAllBe(error => error.Details ==
                "The query was not executed due to a failed transaction" || error.Details ==
                "Cannot COMMIT: Transaction conflict: Write conflict, retry the transaction. This transaction can be retried");
        }
    }
}
