using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace AeroDB.Tests;

public class DocumentSessionWriteLiteralTests
{
    [Test]
    public async Task SaveChangesAsync_WritesNullablePocoNullsAsNone()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();

        surrealSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));

        var session = new DocumentSession(
            client,
            surrealSession,
            new StoreOptions(),
            DocumentTracking.None);

        session.Store(new NullableWriteDocument
        {
            Id = "nullable-write",
            Name = "Nullable Write",
            LockoutEnd = null,
            Tags = ["alpha", "beta"]
        });

        await session.SaveChangesAsync();

        await surrealSession.Received(1).RawQuery(
            Arg.Is<string>(surql =>
                surql.Contains("UPSERT nullable_write_document:`nullable-write` CONTENT", StringComparison.Ordinal)
                && surql.Contains("LockoutEnd: NONE", StringComparison.Ordinal)
                && surql.Contains("Tags: ['alpha', 'beta']", StringComparison.Ordinal)
                && !surql.Contains("$data", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(parameters => parameters == null),
            Arg.Any<CancellationToken>());
    }

    private sealed class NullableWriteDocument
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset? LockoutEnd { get; set; }
        public string[] Tags { get; set; } = [];
    }
}
