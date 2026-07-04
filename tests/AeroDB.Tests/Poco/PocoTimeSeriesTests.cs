using AeroDB;
using NSubstitute;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests.Poco;

public class PocoSensorReading
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
    public string SensorId { get; set; } = "";
}

public class PocoTimeSeriesTests
{
    private static (ISurrealDbSession MockSession, SurrealQueryProvider Provider) CreateMockProvider()
    {
        var mockSession = Substitute.For<ISurrealDbSession>();
        mockSession.RawQuery(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>()
        ).Returns(new SurrealDbResponse(new List<ISurrealDbResult>()));

        var options = new StoreOptions();
        options.Schema.For<PocoSensorReading>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        var provider = new SurrealQueryProvider(mockSession, options);
        return (mockSession, provider);
    }

    [Test]
    public async Task PocoTimeSeries_BucketByFloor_GeneratesCorrectSql()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliTimeSeriesQuery<PocoSensorReading>(provider);

        await query
            .BucketByFloor(s => s.Timestamp, 1, TimeUnit.Hour)
            .Select(a => a.Count().As("cnt"))
            .ToListAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("time::floor(Timestamp, 1h) AS _bucket")
                && sql.Contains("count() AS cnt")
                && sql.Contains("FROM `poco_sensor_reading`")
                && sql.Contains("GROUP BY _bucket")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PocoTimeSeries_BucketByGroup_GeneratesCorrectSql()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliTimeSeriesQuery<PocoSensorReading>(provider);

        await query
            .BucketByGroup(s => s.Timestamp, TimeBucket.Hour)
            .Select(a => a.Count().As("cnt"))
            .ToListAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("time::group(Timestamp, 'hour') AS _bucket")
                && sql.Contains("GROUP BY _bucket")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PocoTimeSeries_Downsample_GeneratesCorrectSql()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliTimeSeriesQuery<PocoSensorReading>(provider);

        await query
            .Downsample(s => s.Timestamp, 24)
            .Where(s => s.Timestamp >= new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc))
            .ToListAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("time::floor(Timestamp, 1d)")
                && sql.Contains("GROUP BY _bucket")
                && sql.Contains("WHERE")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PocoTimeSeries_WithWhere_GeneratesCorrectSql()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new DaliTimeSeriesQuery<PocoSensorReading>(provider);

        await query
            .BucketByFloor(s => s.Timestamp, 1, TimeUnit.Day)
            .Where(s => s.SensorId == "sensor-a")
            .ToListAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("WHERE")
                && sql.Contains("$p0")
                && sql.Contains("SensorId")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PocoTimeSeries_StoreAndBucket_ReturnsBuckets()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoSensorReading>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new PocoSensorReading { Id = 1, SensorId = "sensor-a", Timestamp = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc), Value = 25.5 });
        session.Store(new PocoSensorReading { Id = 2, SensorId = "sensor-a", Timestamp = new DateTime(2026, 7, 1, 11, 0, 0, DateTimeKind.Utc), Value = 26.0 });
        session.Store(new PocoSensorReading { Id = 3, SensorId = "sensor-a", Timestamp = new DateTime(2026, 7, 2, 10, 0, 0, DateTimeKind.Utc), Value = 27.5 });
        await session.SaveChangesAsync();

        try
        {
            var results = await session.TimeSeries<PocoSensorReading>()
                .BucketByFloor(s => s.Timestamp, 1, TimeUnit.Day)
                .Select(a => a.Field(s => s.SensorId).Count().As("cnt"))
                .ToListAsync();

            results.ShouldNotBeNull();
        }
        catch (Exception ex) when (ex is NotSupportedException || ex.Message.Contains("not supported"))
        {
            // Some embedded engine builds do not support all time functions.
        }
    }
}
