using AeroDB.Sable;
using AeroDB.Tests.Poco;
using NSubstitute;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

public sealed class IdentityQueryTranslationTests
{
    [Test]
    public void Visitor_renders_native_record_ordering_for_scalar_identity_and_preserves_keyset_filtering()
    {
        var options = CreateOptions();
        var source = CreateQuery<PocoInt>(options);
        const int afterId = 42;

        var query = source
            .Where(candidate => candidate.Id > afterId)
            .OrderBy(candidate => candidate.Id);

        var result = new SurrealExpressionVisitor(options.Schema).Translate(query.Expression);

        result.ToSurrealQL().ShouldBe(
            "SELECT * FROM `poco_int` WHERE <int> meta::id(id) > $p0 ORDER BY id ASC;");
        result.Parameters["p0"].ShouldBe(afterId);
    }

    [Test]
    public void Queryable_command_uses_the_same_scalar_identity_expression()
    {
        var options = CreateOptions();
        var source = CreateQuery<PocoInt>(options);

        var command = source.OrderBy(candidate => candidate.Id).ToCommand();

        command.CommandText.ShouldBe(
            "SELECT * FROM `poco_int` ORDER BY id ASC;");
        command.Parameters.ShouldBeEmpty();
    }

    [Test]
    public void Queryable_command_orders_long_string_and_guid_identity_by_the_native_record_id()
    {
        var longOptions = new StoreOptions();
        longOptions.Schema.For<PocoLong>().Identity(candidate => candidate.Id);
        var stringOptions = new StoreOptions();
        stringOptions.Schema.For<PocoString>().Identity(candidate => candidate.Id);
        var guidOptions = new StoreOptions();
        guidOptions.Schema.For<PocoGuid>().Identity(candidate => candidate.Id);

        CreateQuery<PocoLong>(longOptions).OrderBy(candidate => candidate.Id).ToCommand().CommandText
            .ShouldBe("SELECT * FROM `poco_long` ORDER BY id ASC;");
        CreateQuery<PocoString>(stringOptions).OrderBy(candidate => candidate.Id).ToCommand().CommandText
            .ShouldBe("SELECT * FROM `poco_string` ORDER BY id ASC;");
        CreateQuery<PocoGuid>(guidOptions).OrderBy(candidate => candidate.Id).ToCommand().CommandText
            .ShouldBe("SELECT * FROM `poco_guid` ORDER BY id ASC;");
    }

    private static StoreOptions CreateOptions()
    {
        var options = new StoreOptions();
        options.Schema.For<PocoInt>().Identity(candidate => candidate.Id);
        return options;
    }

    private static ISableQueryable<T> CreateQuery<T>(StoreOptions options)
        where T : class
    {
        var session = Substitute.For<SurrealDb.Net.ISurrealDbSession>();
        session.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));

        return new SableQueryable<T>(new SurrealQueryProvider(session, options));
    }
}
