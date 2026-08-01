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
