using AeroDB.WolverineFx;
using SurrealDb.Net.Models.Errors;
using TUnit.Core;

namespace AeroDB.Tests;

public sealed class AeroDBAllocationRetryTests
{
    private const string Conflict = "Cannot COMMIT: Transaction conflict: Write conflict, retry the transaction. This transaction can be retried";
    private const string NotExecuted = "The query was not executed due to a failed transaction";

    [Test]
    public void Exact_confirmed_rollback_accepts_more_than_one_unexecuted_statement()
    {
        AeroDBMessageStore.IsConfirmedAllocationRollbackConflict(
            [(RpcErrorKind.Query, NotExecuted), (RpcErrorKind.Query, NotExecuted), (RpcErrorKind.Query, Conflict)])
            .ShouldBeTrue();
        AeroDBMessageStore.IsConfirmedAllocationRollbackConflict(
            [(RpcErrorKind.Query, NotExecuted), (RpcErrorKind.Query, Conflict)])
            .ShouldBeTrue();
    }

    [Test]
    public void Other_failures_and_unconfirmed_commit_outcomes_are_never_retryable()
    {
        (RpcErrorKind Kind, string? Details)[][] denied =
        [
            [],
            [(RpcErrorKind.Query, Conflict)],
            [(RpcErrorKind.Query, NotExecuted), (RpcErrorKind.Query, NotExecuted)],
            [(RpcErrorKind.Thrown, NotExecuted), (RpcErrorKind.Thrown, Conflict)],
            [(RpcErrorKind.Connection, NotExecuted), (RpcErrorKind.Query, Conflict)],
            [(RpcErrorKind.Query, "Cannot COMMIT: network outcome unknown"), (RpcErrorKind.Query, NotExecuted)],
            [(RpcErrorKind.Query, "forged " + Conflict), (RpcErrorKind.Query, NotExecuted)],
            [(RpcErrorKind.Query, Conflict + " trailing content"), (RpcErrorKind.Query, NotExecuted)],
            [(RpcErrorKind.Query, Conflict), (RpcErrorKind.Query, Conflict)],
            [(RpcErrorKind.Query, Conflict), (RpcErrorKind.Query, "other database failure")],
            [(RpcErrorKind.Query, Conflict), (RpcErrorKind.Query, null)]
        ];

        foreach (var errors in denied)
            AeroDBMessageStore.IsConfirmedAllocationRollbackConflict(errors).ShouldBeFalse();
    }
}
