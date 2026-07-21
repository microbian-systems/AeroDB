using AeroDB.Sable;
using Shouldly;

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
}
