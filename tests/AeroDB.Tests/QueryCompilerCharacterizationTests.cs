using AeroDB.Sable;
using NSubstitute;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Freezes the current LINQ-to-SurrealQL behavior at the compiler boundary so
/// the AST refactor can change internals without silently changing commands.
/// </summary>
public sealed class QueryCompilerCharacterizationTests
{
    [Test]
    public void Ordinary_linq_freezes_command_text_and_parameters()
    {
        var (source, _) = CreateQuery<Person>();
        var minimumAge = 21;
        var namePrefix = "A";

        var query = source
            .Where(person => person.Age >= minimumAge && person.Name.StartsWith(namePrefix))
            .OrderByDescending(person => person.Age)
            .ThenBy(person => person.Name)
            .Skip(5)
            .Take(10);

        var result = new SurrealExpressionVisitor().Translate(query.Expression);

        result.ToSurrealQL().ShouldBe(
            "SELECT * FROM `person` WHERE (age >= $p0) AND (string::starts_with(name, $p1)) " +
            "ORDER BY age DESC, name ASC LIMIT 10 START 5;");
        result.Parameters.Count.ShouldBe(2);
        result.Parameters["p0"].ShouldBe(minimumAge);
        result.Parameters["p1"].ShouldBe(namePrefix);
    }

    [Test]
    public void Projection_freezes_aliases_and_source_parameters()
    {
        var (source, _) = CreateQuery<Person>();
        var minimumAge = 30;

        var query = source
            .Where(person => person.Age >= minimumAge)
            .Select(person => new PersonSummary
            {
                DisplayName = person.Name,
                Years = person.Age
            });

        var result = new SurrealExpressionVisitor().Translate(query.Expression);

        result.ToSurrealQL().ShouldBe(
            "SELECT name AS DisplayName, age AS Years FROM `person` WHERE age >= $p0;");
        result.Parameters.Count.ShouldBe(1);
        result.Parameters["p0"].ShouldBe(minimumAge);
    }

    [Test]
    public void Grouping_freezes_current_group_key_translation()
    {
        var (source, _) = CreateQuery<Product>();

        var query = source.GroupBy(product => new { product.Category, product.Quantity });
        var result = new SurrealExpressionVisitor().Translate(query.Expression);

        // Known current limitation: GroupBy records CLR member names instead of
        // resolving storage names. This assertion is a migration tripwire, not
        // the desired AST behavior; replace it when grouping enters the AST path.
        result.ToSurrealQL().ShouldBe(
            "SELECT * FROM `product` GROUP BY Category, Quantity;");
        result.Parameters.ShouldBeEmpty();
    }

    [Test]
    public async Task Count_freezes_terminal_rewrite_and_parameters()
    {
        var (source, session) = CreateQuery<Person>();
        var minimumAge = 18;

        var count = await source.Where(person => person.Age >= minimumAge).CountAsync();

        count.ShouldBe(0);
        await session.Received(1).RawQuery(
            "SELECT count() FROM `person` WHERE age >= $p0 GROUP ALL;",
            Arg.Is<IReadOnlyDictionary<string, object?>>(parameters =>
                parameters.Count == 1 && Equals(parameters["p0"], minimumAge)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Any_freezes_terminal_rewrite_and_parameters()
    {
        var (source, session) = CreateQuery<Person>();
        var name = "Alice";

        var any = await source.Where(person => person.Name == name).AnyAsync();

        any.ShouldBeFalse();
        await session.Received(1).RawQuery(
            "SELECT * FROM `person` WHERE name = $p0 LIMIT 1;",
            Arg.Is<IReadOnlyDictionary<string, object?>>(parameters =>
                parameters.Count == 1 && Equals(parameters["p0"], name)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void Policy_injection_freezes_current_predicate_order()
    {
        var options = new StoreOptions { TenancyStyle = TenancyStyle.Conjoined };
        var (source, _) = CreateQuery<PolicyDocument>(options, "tenant-a");

        var command = source
            .Where(document => document.Name == "active")
            .ToCommand();

        // Known current limitation: the tenant value is escaped and inlined while
        // the user predicate is parameterized. The AST compiler should parameterize
        // both; this baseline makes that deliberate behavior change visible.
        command.CommandText.ShouldBe(
            "SELECT * FROM `policy_document` WHERE name = $p0 " +
            "AND tenant_id = 'tenant-a' AND deleted = false;");
        command.Parameters.Count.ShouldBe(1);
        command.Parameters["p0"].ShouldBe("active");
    }

    [Test]
    public void Compiled_query_freezes_template_and_property_mapping()
    {
        var query = new FindPersonByFirstName { FirstName = "Alice" };

        var plan = CompiledQueryPlanner.GetOrBuildPlan<Person, Person>(query);

        plan.SkeletonResult.ToSurrealQL().ShouldBe(
            "SELECT * FROM `person` WHERE name = $p0;");
        plan.SkeletonResult.Parameters.Count.ShouldBe(1);
        plan.ParameterMapping.Count.ShouldBe(1);
        plan.ParameterMapping["p0"].ShouldBe(nameof(FindPersonByFirstName.FirstName));
        plan.IsSingleResult.ShouldBeTrue();
    }

    private static (ISableQueryable<T> Query, SurrealDb.Net.ISurrealDbSession Session)
        CreateQuery<T>(StoreOptions? options = null, string? tenantId = null)
        where T : class
    {
        var session = Substitute.For<SurrealDb.Net.ISurrealDbSession>();
        session.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));

        var provider = new SurrealQueryProvider(session, options ?? new StoreOptions(), tenantId);
        return (new SableQueryable<T>(provider), session);
    }

    private sealed class PersonSummary
    {
        public string DisplayName { get; set; } = "";
        public int Years { get; set; }
    }

    public sealed class PolicyDocument : Record, ISoftDeleted
    {
        public string Name { get; set; } = "";
        public string TenantId { get; set; } = "";
        public bool Deleted { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }
}
