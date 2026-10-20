using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

public class EmbeddedTransactionParameterTests
{
    [Test]
    public async Task EmbeddedTransaction_TransactionLocalParametersThenCancel_DiscardsRecord()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        const string id = "provider_transaction_parameter_cancel";

        // Create the table outside the transaction under test. The assertion below
        // concerns rollback of this record, not missing-table query behavior.
        session.Store(new Person { Name = "parameter-transaction-table-bootstrap", Age = 1 });
        await session.SaveChangesAsync();

        await using var transaction = await session.Session.BeginTransaction();
        await transaction.Set("id", id);
        await transaction.Set("name", "MustBeDiscarded");
        var response = await transaction.RawQuery(
            "CREATE type::record('person', $id) CONTENT { name: $name, age: 42 };",
            null);
        response.HasErrors.ShouldBeFalse();
        await transaction.Cancel();

        await using var verify = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.LoadAsync<Person>(id)).ShouldBeNull();
    }
}
