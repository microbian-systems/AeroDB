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
    public async Task SaveChangesAsync_BindsDeepPocosInsteadOfExpandingTheQuerySyntaxTree()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var surrealSession = Substitute.For<ISurrealDbSession>();
        surrealSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));
        var session = new DocumentSession(client, surrealSession, new StoreOptions(), DocumentTracking.None);
        session.Store(new DeepWriteDocument
        {
            Id = "deep-write",
            Content = CreateNestedContent(12)
        });

        await session.SaveChangesAsync();

        await surrealSession.Received(1).RawQuery(
            Arg.Is<string>(surql =>
                surql.Contains("content: $__sable_value_0", StringComparison.Ordinal)
                && !surql.Contains("level-12", StringComparison.Ordinal)),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(parameters =>
                HasExpectedDeepValueParameter(parameters)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveChangesAsync_RoundTripsDeepPocosThroughBoundParameters()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
            options.Schema.For<DeepWriteDocument>().Identity(document => document.Id));
        await using (var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(new DeepWriteDocument
            {
                Id = "deep-round-trip",
                Content = CreateNestedContent(12)
            });
            await session.SaveChangesAsync();
        }

        await using var read = await store.QuerySessionAsync();
        var loaded = await read.LoadAsync<DeepWriteDocument>("deep-round-trip");

        loaded.ShouldNotBeNull();
        loaded.Content.Name.ShouldBe("level-12");
        var leaf = loaded.Content;
        while (leaf.Children.Count > 0)
            leaf = leaf.Children[0];

        leaf.Name.ShouldBe("level-0");
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

    private sealed class DeepWriteDocument
    {
        public string Id { get; set; } = "";
        public DeepWriteContent Content { get; set; } = new();
    }

    private sealed class DeepWriteContent
    {
        public string Name { get; set; } = "";
        public List<DeepWriteContent> Children { get; set; } = [];
    }

    private static DeepWriteContent CreateNestedContent(int level)
    {
        var content = new DeepWriteContent { Name = $"level-{level}" };
        if (level > 0)
            content.Children.Add(CreateNestedContent(level - 1));

        return content;
    }

    private static bool HasExpectedDeepValueParameter(
        IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null
            || !parameters.TryGetValue("__sable_value_0", out var value)
            || value is not JsonElement element
            || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return element.GetProperty("name").GetString() == "level-12";
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
