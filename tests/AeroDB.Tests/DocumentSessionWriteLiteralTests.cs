using AeroDB.Sable;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;
using System.Text.Json;

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
                && surql.Contains("lockout_end: NONE", StringComparison.Ordinal)
                && surql.Contains("tags: ['alpha', 'beta']", StringComparison.Ordinal)
                && !surql.Contains("$data", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(parameters => parameters == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveChangesAsync_WritesNestedPocosAsObjectLiterals()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();
        surrealSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));
        var session = new DocumentSession(client, surrealSession, new StoreOptions(), DocumentTracking.None);
        session.Store(new ComplexWriteDocument
        {
            Id = "complex-write",
            Content = new ComplexWriteContent
            {
                Title = "Nested",
                Layout = new ComplexWriteLayout { Columns = 2 }
            }
        });

        await session.SaveChangesAsync();

        await surrealSession.Received(1).RawQuery(
            Arg.Is<string>(surql =>
                surql.Contains("content: { title: 'Nested', layout: { columns: 2 } }", StringComparison.Ordinal)
                && !surql.Contains("content: '{", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(parameters => parameters == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveChangesAsync_QuotesDictionaryKeysThatAreNotSurrealQlIdentifiers()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();
        surrealSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));
        var session = new DocumentSession(client, surrealSession, new StoreOptions(), DocumentTracking.None);
        session.Store(new DictionaryWriteDocument
        {
            Id = "dictionary-write",
            Fields = new Dictionary<string, JsonElement>
            {
                ["title-2"] = JsonSerializer.SerializeToElement("test"),
                ["simple"] = JsonSerializer.SerializeToElement("plain")
            }
        });

        await session.SaveChangesAsync();

        await surrealSession.Received(1).RawQuery(
            Arg.Is<string>(surql =>
                surql.Contains("`title-2`: 'test'", StringComparison.Ordinal)
                && surql.Contains("simple: 'plain'", StringComparison.Ordinal)
                && !surql.Contains("title-2: 'test'", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(parameters => parameters == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveChangesAsync_RoundTripsDictionaryJsonElementsWithRichTextKeys()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
            options.Schema.For<DictionaryWriteDocument>().Identity(document => document.Id));
        await using (var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(new DictionaryWriteDocument
            {
                Id = "dictionary-literal",
                Fields = new Dictionary<string, JsonElement>
                {
                    ["simple"] = Json("\"plain text\""),
                    ["rich-text"] = Json("\"<p>Hello <strong>SurrealDB</strong></p>\""),
                    ["number"] = Json("42.5"),
                    ["enabled"] = Json("true"),
                    ["metadata"] = Json("""{"author":{"name":"Ada"},"tags":["cms","sable"]}"""),
                    ["sections"] = Json("""[{"kind":"hero","columns":2},{"kind":"copy","columns":1}]""")
                }
            });
            await session.SaveChangesAsync();
        }

        await using var read = await store.QuerySessionAsync();
        var loaded = await read.LoadAsync<DictionaryWriteDocument>("dictionary-literal");

        loaded.ShouldNotBeNull();
        loaded.Fields["simple"].GetString().ShouldBe("plain text");
        loaded.Fields["rich-text"].GetString().ShouldBe("<p>Hello <strong>SurrealDB</strong></p>");
        loaded.Fields["number"].GetDecimal().ShouldBe(42.5m);
        loaded.Fields["enabled"].GetBoolean().ShouldBeTrue();
        loaded.Fields["metadata"].GetProperty("author").GetProperty("name").GetString().ShouldBe("Ada");
        loaded.Fields["sections"].GetArrayLength().ShouldBe(2);
    }

    private sealed class NullableWriteDocument
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset? LockoutEnd { get; set; }
        public string[] Tags { get; set; } = [];
    }

    private sealed class ComplexWriteDocument
    {
        public string Id { get; set; } = "";
        public ComplexWriteContent Content { get; set; } = new();
    }

    private sealed class ComplexWriteContent
    {
        public string Title { get; set; } = "";
        public ComplexWriteLayout Layout { get; set; } = new();
    }

    private sealed class ComplexWriteLayout
    {
        public int Columns { get; set; }
    }

    private sealed class DictionaryWriteDocument
    {
        public string Id { get; set; } = "";
        public Dictionary<string, JsonElement> Fields { get; set; } = [];
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
