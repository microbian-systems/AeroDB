using AeroDB.Sable;
using Shouldly;
using System.Text.Json;

namespace AeroDB.Tests;

public class DocumentSessionWriteLiteralTests
{
    [Test]
    public async Task SaveChangesAsync_WritesNullablePocoNullsAsNone()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
            options.Schema.For<NullableWriteDocument>().Identity(document => document.Id));
        await using (var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(new NullableWriteDocument
            {
                Id = "nullable-write",
                Name = "Nullable Write",
                LockoutEnd = null,
                Tags = ["alpha", "beta"]
            });
            await session.SaveChangesAsync();
        }

        await using var read = await store.QuerySessionAsync();
        var loaded = await read.LoadAsync<NullableWriteDocument>("nullable-write");
        loaded.ShouldNotBeNull();
        loaded.LockoutEnd.ShouldBeNull();
        loaded.Tags.ShouldBe(["alpha", "beta"]);
    }

    [Test]
    public async Task SaveChangesAsync_WritesNestedPocosAsObjectLiterals()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
            options.Schema.For<ComplexWriteDocument>().Identity(document => document.Id));
        await using (var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None }))
        {
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
        }

        await using var read = await store.QuerySessionAsync();
        var loaded = await read.LoadAsync<ComplexWriteDocument>("complex-write");
        loaded.ShouldNotBeNull();
        loaded.Content.Title.ShouldBe("Nested");
        loaded.Content.Layout.Columns.ShouldBe(2);
    }

    [Test]
    public async Task SaveChangesAsync_RoundTripsDictionaryJsonElementsWithRichTextKeys()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
            options.Schema.For<DictionaryLiteralDocument>().Identity(document => document.Id));
        await using (var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(new DictionaryLiteralDocument
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
        var loaded = await read.LoadAsync<DictionaryLiteralDocument>("dictionary-literal");

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

    private sealed class DictionaryLiteralDocument
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
