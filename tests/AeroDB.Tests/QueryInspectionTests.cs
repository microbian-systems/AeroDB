using AeroDB.Sable;
using NSubstitute;
using SurrealDb.Net;
using TUnit.Core;

namespace AeroDB.Tests;

public sealed class QueryInspectionTests
{
    [Test]
    public void Queryable_inspection_exposes_text_and_parameters_without_execution()
    {
        var (session, databaseSession) = CreateSession();
        const string secretName = "Alice-secret";

        var query = session.Query<Person>()
            .Where(person => person.Name == secretName)
            .OrderBy(person => person.Age);

        var command = query.ToCommand();

        command.CommandText.ShouldBe(
            "SELECT * FROM `person` WHERE name = $p0 ORDER BY age ASC;");
        command.Parameters.Count.ShouldBe(1);
        command.Parameters["p0"].ShouldBe(secretName);
        command.CommandText.ShouldNotContain(secretName);
        query.ToString().ShouldBe(command.CommandText);
        databaseSession.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public void Concrete_compiled_query_inspection_returns_parameterized_template()
    {
        var store = Documents.For(_ => { });
        const int minimumAge = 27;
        var compiled = store.CompileQuery<Person>(people =>
            people.Where(person => person.Age >= minimumAge));

        var command = compiled.ToCommand();

        command.CommandText.ShouldBe("SELECT * FROM `person` WHERE age >= $p0;");
        command.Parameters.Count.ShouldBe(1);
        command.Parameters["p0"].ShouldBe(minimumAge);
        compiled.ToString().ShouldBe(command.CommandText);
    }

    [Test]
    public void Interface_compiled_query_inspection_binds_runtime_values_without_execution()
    {
        var (session, databaseSession) = CreateSession();
        var compiled = new FindPersonByFirstName { FirstName = "Diana-secret" };

        var command = session.ToCommand(compiled);

        command.CommandText.ShouldBe(
            "SELECT * FROM `person` WHERE name = $p0 LIMIT 1;");
        command.Parameters.Count.ShouldBe(1);
        command.Parameters["p0"].ShouldBe(compiled.FirstName);
        command.CommandText.ShouldNotContain(compiled.FirstName);
        databaseSession.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public void Sable_command_snapshots_the_parameter_dictionary()
    {
        var parameters = new Dictionary<string, object?> { ["p0"] = "before" };
        var command = new SableCommand("SELECT * FROM `person` WHERE name = $p0;", parameters);

        parameters["p0"] = "after";
        parameters["p1"] = 42;

        command.Parameters.Count.ShouldBe(1);
        command.Parameters["p0"].ShouldBe("before");
        command.ToString().ShouldBe(command.CommandText);
    }

    private static (QuerySession Session, ISurrealDbSession DatabaseSession) CreateSession()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var databaseSession = Substitute.For<ISurrealDbSession>();
        var session = new QuerySession(
            client,
            databaseSession,
            new StoreOptions(),
            DocumentTracking.None);

        return (session, databaseSession);
    }
}
