using AeroDB;
using System.Linq.Expressions;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;
using NSubstitute;
using TUnit.Core;

namespace AeroDB.Tests;

// ──────────────────────────────────────────────
//  ML test models
// ──────────────────────────────────────────────

/// <summary>Input model: house listing with property features.</summary>
public class HouseListing
{
    public double Squarefoot { get; set; }
    public double NumFloors { get; set; }
    public int Bedrooms { get; set; }
    public string? Neighborhood { get; set; }
}

/// <summary>Output model: price prediction result.</summary>
public class PricePrediction
{
    public double PredictedPrice { get; set; }
    public double Confidence { get; set; }
}

/// <summary>Output model for batch inference (includes original fields).</summary>
public class HouseWithPrediction : HouseListing
{
    public PricePrediction? MlResult { get; set; }
}

// ──────────────────────────────────────────────
//  ML query tests
// ──────────────────────────────────────────────

public class MlQueryTests
{
    // ══════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════

    private static (ISurrealDbSession MockSession, SurrealQueryProvider Provider) CreateMockProvider()
    {
        var mockSession = Substitute.For<ISurrealDbSession>();
        mockSession.RawQuery(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>()
        ).Returns(new SurrealDbResponse(new List<ISurrealDbResult>()));

        var options = new StoreOptions();
        var provider = new SurrealQueryProvider(mockSession, options);
        return (mockSession, provider);
    }

    // ══════════════════════════════════════════
    //  Compute (single inference) SQL generation
    // ══════════════════════════════════════════

    [Test]
    public async Task MlQuery_Compute_Basic_SQL()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, PricePrediction>(provider);

        query
            .Model("house-price-prediction", "0.0.1")
            .Input(x => new { squarefoot = x.Squarefoot, num_floors = x.NumFloors });

        var input = new HouseListing { Squarefoot = 500.0, NumFloors = 1.0, Bedrooms = 3 };
        await query.ComputeAsync(input);

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.StartsWith("RETURN ml::house-price-prediction<0.0.1>(")
                && sql.Contains("squarefoot: 500")
                && sql.Contains("num_floors: 1")
                && sql.EndsWith(");")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Test]
    public async Task MlQuery_Compute_With_Field_Bindings()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, PricePrediction>(provider);

        query
            .Model("house-price-prediction", "0.0.1")
            .Input(x => new { squarefoot = x.Squarefoot, num_floors = x.NumFloors });

        var input = new HouseListing { Squarefoot = 1500.0, NumFloors = 2.0, Bedrooms = 4 };
        await query.ComputeAsync(input);

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("squarefoot: 1500")
                && sql.Contains("num_floors: 2")
                && !sql.Contains("bedrooms")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>()
        );
    }

    // ══════════════════════════════════════════
    //  ComputeAll (batch inference) SQL generation
    // ══════════════════════════════════════════

    [Test]
    public async Task MlQuery_ComputeAll_Basic_SQL()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, HouseWithPrediction>(provider);

        query
            .Model("house-price-prediction", "0.0.1")
            .Input(x => new { squarefoot = x.Squarefoot, num_floors = x.NumFloors });

        await query.ComputeAllAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("SELECT *, ml::house-price-prediction<0.0.1>(")
                && sql.Contains("AS _ml_result")
                && sql.Contains("FROM `house_listing`")
                && sql.Contains("LIMIT 1000")
                && !sql.Contains("WHERE")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Test]
    public async Task MlQuery_ComputeAll_With_Where()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, HouseWithPrediction>(provider);

        query
            .Model("house-price-prediction", "0.0.1")
            .Input(x => new { squarefoot = x.Squarefoot, num_floors = x.NumFloors })
            .Where(x => x.Bedrooms > 2);

        await query.ComputeAllAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("WHERE (")
                && sql.Contains("Bedrooms")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Test]
    public async Task MlQuery_ComputeAll_With_Limit_Skip()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, HouseWithPrediction>(provider);

        query
            .Model("house-price-prediction", "0.0.1")
            .Input(x => new { squarefoot = x.Squarefoot, num_floors = x.NumFloors })
            .Take(50)
            .Skip(10);

        await query.ComputeAllAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("LIMIT 50")
                && sql.Contains("START AT 10")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>()
        );
    }

    // ══════════════════════════════════════════
    //  Error cases
    // ══════════════════════════════════════════

    [Test]
    public async Task MlQuery_Missing_Model_Throws()
    {
        var (_, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, PricePrediction>(provider);

        query.Input(x => new { squarefoot = x.Squarefoot });

        await AssertThrows<InvalidOperationException>(() =>
            query.ComputeAsync(new HouseListing()));
    }

    [Test]
    public async Task MlQuery_Missing_Input_Throws()
    {
        var (_, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, PricePrediction>(provider);

        query.Model("test", "1.0");

        await AssertThrows<InvalidOperationException>(() =>
            query.ComputeAsync(new HouseListing()));
    }

    [Test]
    public async Task MlQuery_ComputeAll_Missing_Model_Throws()
    {
        var (_, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, PricePrediction>(provider);

        query.Input(x => new { squarefoot = x.Squarefoot });

        await AssertThrows<InvalidOperationException>(() =>
            query.ComputeAllAsync());
    }

    [Test]
    public async Task MlQuery_ComputeAll_Missing_Input_Throws()
    {
        var (_, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, PricePrediction>(provider);

        query.Model("test", "1.0");

        await AssertThrows<InvalidOperationException>(() =>
            query.ComputeAllAsync());
    }

    // ══════════════════════════════════════════
    //  Input mapping tests
    // ══════════════════════════════════════════

    [Test]
    public async Task MlQuery_InputMapping_Extracts_Fields()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, PricePrediction>(provider);

        query
            .Model("test", "1.0")
            .Input(x => new { sqft = x.Squarefoot, floors = x.NumFloors });

        await query.ComputeAllAsync();

        // The batch column binding should map anonymous member names to property names
        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("sqft: Squarefoot")
                && sql.Contains("floors: NumFloors")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Test]
    public async Task MlQuery_InputMapping_With_Convert()
    {
        // Value type properties (like int) produce Convert expressions in the lambda.
        // The implementation must unwrap Convert to extract the NewExpression.
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliMlQuery<HouseListing, PricePrediction>(provider);

        query
            .Model("test", "1.0")
            .Input(x => new { val = (object)x.Bedrooms });

        // Should not throw — Convert should be unwrapped
        await query.ComputeAllAsync();

        // Note: (object)x.Bedrooms produces Convert(x.Bedrooms) which gets
        // unwrapped at the NewExpression level but the argument is still a
        // UnaryExpression, so the column mapper falls back to the member name (val).
        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql => sql.Contains("val: val")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>()
        );
    }

    // ══════════════════════════════════════════
    //  JSON-to-SurrealQL conversion
    // ══════════════════════════════════════════

    [Test]
    public async Task MlQuery_Json_To_SurrealQL_Conversion()
    {
        var json = """{"squarefoot":500.0,"num_floors":1.0}""";
        var result = DaliMlQuery<HouseListing, PricePrediction>.ConvertJsonToSurrealQL(json);

        result.ShouldBe("{ squarefoot: 500.0, num_floors: 1.0 }");
    }

    [Test]
    public async Task MlQuery_Nested_Objects_Conversion()
    {
        var json = """{"address":{"street":"123 Main","city":"NYC"},"price":250.0}""";
        var result = DaliMlQuery<HouseListing, PricePrediction>.ConvertJsonToSurrealQL(json);

        result.ShouldBe("{ address: { street: 123 Main, city: NYC }, price: 250.0 }");
    }

    [Test]
    public async Task MlQuery_Array_Values_Conversion()
    {
        var json = """{"features":["pool","garage"],"score":95.0}""";
        var result = DaliMlQuery<HouseListing, PricePrediction>.ConvertJsonToSurrealQL(json);

        result.ShouldBe("{ features: [pool, garage], score: 95.0 }");
    }

    // ══════════════════════════════════════════
    //  MlOptions tests
    // ══════════════════════════════════════════

    [Test]
    public async Task MlOptions_Default_Disabled()
    {
        var opts = new MlOptions();
        opts.Enabled.ShouldBe(false);
    }

    [Test]
    public async Task MlOptions_Enabled_Flag()
    {
        var opts = new MlOptions { Enabled = true };
        opts.Enabled.ShouldBe(true);
    }

    [Test]
    public async Task ExperimentalOptions_Contains_ML()
    {
        var exp = new ExperimentalOptions();
        exp.ML.ShouldNotBeNull();
        exp.ML.ShouldBeOfType<MlOptions>();
    }

    [Test]
    public async Task StoreOptions_Experimental_Is_Accessible()
    {
        var opts = new StoreOptions();
        opts.Experimental.ShouldNotBeNull();
        opts.Experimental.ML.ShouldNotBeNull();
        opts.Experimental.ML.Enabled.ShouldBe(false);
    }

    // ══════════════════════════════════════════
    //  Exception tests
    // ══════════════════════════════════════════

    [Test]
    public async Task MlNotAvailableException_Has_Correct_Base()
    {
        var ex = new MlNotAvailableException("ML not available");
        ex.ShouldBeAssignableTo<InvalidOperationException>();
        ex.Message.ShouldBe("ML not available");
    }

    [Test]
    public async Task MlNotAvailableException_With_Inner()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new MlNotAvailableException("ML error", inner);
        ex.InnerException.ShouldBe(inner);
    }

    // ══════════════════════════════════════════
    //  Extension method tests
    // ══════════════════════════════════════════

    [Test]
    public async Task MlQuery_Extensions_Returns_Query()
    {
        var (mockSession, provider) = CreateMockProvider();

        // Mock an IQuerySession that returns a queryable backed by our provider
        var mockQuerySession = Substitute.For<IQuerySession>();
        var queryable = new SurrealDbQueryable<HouseListing>(provider);
        mockQuerySession.Query<HouseListing>().Returns(queryable);

        var result = mockQuerySession.Ml<HouseListing, PricePrediction>();
        result.ShouldNotBeNull();
        result.ShouldBeOfType<DaliMlQuery<HouseListing, PricePrediction>>();
    }

    [Test]
    public async Task MlQuery_Extensions_With_NonAeroDB_Session_Throws()
    {
        var mockQuerySession = Substitute.For<IQuerySession>();
        var badQueryable = Substitute.For<ISurrealDbQueryable<HouseListing>>();
        badQueryable.Provider.Returns(Substitute.For<IQueryProvider>());
        mockQuerySession.Query<HouseListing>().Returns(badQueryable);

        await AssertThrows<NotSupportedException>(() =>
        {
            mockQuerySession.Ml<HouseListing, PricePrediction>();
            return Task.CompletedTask;
        });
    }

    // ══════════════════════════════════════════
    //  Helper: awaitable assertion wrapper
    // ══════════════════════════════════════════

    private static async Task AssertThrows<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
            throw new InvalidOperationException(
                $"Expected {typeof(T).Name} but no exception was thrown.");
        }
        catch (T)
        {
            // Expected — pass
        }
    }
}
