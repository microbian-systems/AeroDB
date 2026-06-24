using System.Linq.Expressions;
using TUnit.Core;

namespace Dali.Tests;

/// <summary>
/// Tests for AggregateQueryBuilder delegation used in time-series queries.
/// Verifies that aggregate expressions (count, sum, average, min, max)
/// produce correct SurrealQL select clauses.
/// </summary>
public class TimeSeriesAggregationTests
{
    [Test]
    public async Task Aggregation_Delegation_Count()
    {
        var builder = new AggregateQueryBuilder<SensorReading>();

        builder.Count().As("n");

        builder.BuildSelect().ShouldBe("count() AS n");
    }

    [Test]
    public async Task Aggregation_Delegation_Sum()
    {
        var builder = new AggregateQueryBuilder<SensorReading>();

        builder.Sum(s => s.Value).As("total");

        builder.BuildSelect().ShouldBe("math::sum(Value) AS total");
    }

    [Test]
    public async Task Aggregation_Delegation_Average()
    {
        var builder = new AggregateQueryBuilder<SensorReading>();

        builder.Average(s => s.Value).As("avg_val");

        builder.BuildSelect().ShouldBe("math::mean(Value) AS avg_val");
    }

    [Test]
    public async Task Aggregation_Delegation_Min()
    {
        var builder = new AggregateQueryBuilder<SensorReading>();

        builder.Min(s => s.Value).As("min_val");

        builder.BuildSelect().ShouldBe("math::min(Value) AS min_val");
    }

    [Test]
    public async Task Aggregation_Delegation_Max()
    {
        var builder = new AggregateQueryBuilder<SensorReading>();

        builder.Max(s => s.Value).As("max_val");

        builder.BuildSelect().ShouldBe("math::max(Value) AS max_val");
    }

    [Test]
    public async Task Aggregation_Multiple_Fields()
    {
        var builder = new AggregateQueryBuilder<SensorReading>();

        builder
            .Count().As("cnt")
            .Sum(s => s.Value).As("total")
            .Average(s => s.Value).As("avg")
            .Min(s => s.Value).As("minimum")
            .Max(s => s.Value).As("maximum");

        var select = builder.BuildSelect();
        select.ShouldContain("count() AS cnt");
        select.ShouldContain("math::sum(Value) AS total");
        select.ShouldContain("math::mean(Value) AS avg");
        select.ShouldContain("math::min(Value) AS minimum");
        select.ShouldContain("math::max(Value) AS maximum");
    }

    [Test]
    public async Task Aggregation_No_PendingAlias()
    {
        var builder = new AggregateQueryBuilder<SensorReading>();

        builder.Count().As("cnt");
        // Second .As() without pending expression should throw
        Should.Throw<InvalidOperationException>(() => builder.As("duplicate"));
    }

    [Test]
    public async Task Aggregation_Raw_Expression()
    {
        var builder = new AggregateQueryBuilder<SensorReading>();

        builder.Raw("math::round(math::mean(Value), 2)").As("rounded_avg");

        builder.BuildSelect().ShouldBe("math::round(math::mean(Value), 2) AS rounded_avg");
    }

    [Test]
    public async Task Aggregation_With_Field()
    {
        var builder = new AggregateQueryBuilder<SensorReading>();

        builder
            .Field(s => s.SensorId)
            .Count().As("cnt");

        builder.BuildSelect().ShouldBe("SensorId, count() AS cnt");
    }
}
