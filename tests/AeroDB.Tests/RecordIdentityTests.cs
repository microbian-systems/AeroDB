using AeroDB.Sable;
using Shouldly;
using SurrealDb.Net.Models;

namespace AeroDB.Tests;

public sealed class RecordIdentityTests
{
    [Test]
    public void LongIdentityRendersAsNativeNumericRecordKey()
    {
        var identity = DocumentIdentityResolver.TryCreate(
            1_501_688_860_171_780_096L,
            "product",
            out var resolved);

        identity.ShouldBeTrue();
        resolved.RecordId.ShouldBeOfType<RecordIdOf<long>>();
        resolved.Literal.ShouldBe("product:1501688860171780096");
    }

    [Test]
    public void NumericLookingStringIdentityRemainsQuoted()
    {
        DocumentIdentityResolver.TryCreate("42", "customer", out var resolved).ShouldBeTrue();

        resolved.RecordId.ShouldBeAssignableTo<RecordIdOf<string>>();
        resolved.Literal.ShouldBe("customer:`42`");
    }

    [Test]
    public void GuidIdentityUsesQuotedStringRecordKey()
    {
        var guid = Guid.Parse("57cd29ad-951a-4187-a4d1-bb7d49f43a7f");

        DocumentIdentityResolver.TryCreate(guid, "session", out var resolved).ShouldBeTrue();

        resolved.RecordId.ShouldBeAssignableTo<RecordIdOf<string>>();
        resolved.Literal.ShouldBe("session:`57cd29ad-951a-4187-a4d1-bb7d49f43a7f`");
    }

    [Test]
    public void StringOverloadInfersDeclaredLongIdentityType()
    {
        var options = new StoreOptions();
        options.Schema.For<LongIdentityDocument>().Identity(x => x.Id);

        var normalized = DocumentIdentityResolver.NormalizeForDocumentType(
            typeof(LongIdentityDocument),
            "42",
            options.Schema);

        normalized.ShouldBeOfType<long>();
        normalized.ShouldBe(42L);
    }

    [Test]
    public void StringOverloadPreservesDeclaredStringIdentityType()
    {
        var options = new StoreOptions();
        options.Schema.For<StringIdentityDocument>().Identity(x => x.Id);

        var normalized = DocumentIdentityResolver.NormalizeForDocumentType(
            typeof(StringIdentityDocument),
            "42",
            options.Schema);

        normalized.ShouldBeOfType<string>();
        normalized.ShouldBe("42");
    }

    private sealed class LongIdentityDocument
    {
        public long Id { get; set; }
    }

    private sealed class StringIdentityDocument
    {
        public string Id { get; set; } = string.Empty;
    }
}
