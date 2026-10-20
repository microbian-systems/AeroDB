using Shouldly;
using SurrealDb.Embedded.InMemory;
using TUnit.Core;

namespace AeroDB.Tests;

[NotInParallel]
public sealed class EmbeddedDriverCancellationRegressionTests
{
    [Test]
    public async Task Cancelling_the_original_token_after_a_completed_embedded_query_does_not_invoke_a_disposed_timeout_source()
    {
        await using var client = new SurrealDbMemoryClient();
        await client.Use("driver_cancellation_regression", "query");
        using var caller = new CancellationTokenSource();

        var response = await client.RawQuery("RETURN 1;", cancellationToken: caller.Token);
        response.EnsureAllOks();

        Should.NotThrow(() => caller.Cancel());
    }
}
