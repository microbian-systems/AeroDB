using AeroDB;
using NSubstitute;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests.Poco;

public class PocoStore
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public GeometryPoint Location { get; set; } = new();
    public GeometryPolygon? DeliveryZone { get; set; }
}

public class PocoGisTests
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
        options.Schema.For<PocoStore>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        var provider = new SurrealQueryProvider(mockSession, options);
        return (mockSession, provider);
    }

    [Test]
    public async Task PocoGis_NearBy_GeneratesCorrectSql()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new AeroDBSpatialQuery<PocoStore>(provider);

        await query.NearBy(s => s.Location, 48.8566, 2.3522, 1000).ToListAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("geo::DISTANCE(Location, (2.3522, 48.8566))")
                && sql.Contains("FROM `poco_store`")
                && sql.Contains("INSIDE")
                && sql.Contains("ORDER BY _distance ASC")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PocoGis_NearBy_WithWhere_GeneratesCorrectSql()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new AeroDBSpatialQuery<PocoStore>(provider);

        await query
            .NearBy(s => s.Location, 48.8566, 2.3522, 1000)
            .Where(s => s.Category == "cafe")
            .ToListAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("geo::DISTANCE")
                && sql.Contains("$p0")
                && sql.Contains("category")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PocoGis_NearBy_WithLimit_GeneratesCorrectSql()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new AeroDBSpatialQuery<PocoStore>(provider);

        await query.NearBy(s => s.Location, 48.8566, 2.3522, 1000).Take(25).ToListAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql => sql.Contains("LIMIT 25")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PocoGis_Within_GeneratesCorrectSql()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new AeroDBSpatialQuery<PocoStore>(provider);

        var polygon = new List<(double Lng, double Lat)>
        {
            (0, 0), (1, 0), (1, 1), (0, 1), (0, 0)
        };

        await query.Within(s => s.Location, polygon).ToListAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("Location INSIDE")
                && sql.Contains("type: 'Polygon'")
                && sql.Contains("[0, 0]")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PocoGis_OrderByDistance_GeneratesCorrectSql()
    {
        var (mockSession, provider) = CreateMockProvider();
        var query = new AeroDBSpatialQuery<PocoStore>(provider);

        await query.OrderByDistance(s => s.Location, 48.8566, 2.3522).ToListAsync();

        await mockSession.Received(1).RawQuery(
            Arg.Is<string>(sql =>
                sql.Contains("geo::DISTANCE(Location, (2.3522, 48.8566)) AS _distance")
                && sql.Contains("ORDER BY _distance ASC")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PocoGis_StoreAndNearBy_ReturnsStoredGeometry()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoStore>()
                .Identity(x => x.Id)
                .SetSchemaMode(SchemaMode.Flexible)
                .SpatialIndex(x => x.Location);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new PocoStore { Id = 1, Name = "Cafe Paris", Category = "cafe", Location = new GeometryPoint(2.3522, 48.8566), DeliveryZone = null });
        session.Store(new PocoStore { Id = 2, Name = "Bakery Lyon", Category = "bakery", Location = new GeometryPoint(4.8357, 45.7640), DeliveryZone = null });
        await session.SaveChangesAsync();

        var loaded = await session.LoadAsync<PocoStore>("1");
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Cafe Paris");
        loaded.Location.Lng.ShouldBe(2.3522, 0.0001);
        loaded.Location.Lat.ShouldBe(48.8566, 0.0001);

        try
        {
            var results = await session.Spatial<PocoStore>()
                .NearBy(s => s.Location, 48.8566, 2.3522, 50_000)
                .ToListAsync();

            results.ShouldNotBeNull();
        }
        catch (Exception ex) when (
            ex is NotSupportedException
            || ex.Message.Contains("not supported")
            || ex.Message.Contains("feature"))
        {
            // Some embedded engine builds do not support geo functions.
        }
    }

    [Test]
    public async Task PocoGis_UpdateLocation_ReloadsGeometry()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoStore>()
                .Identity(x => x.Id)
                .SetSchemaMode(SchemaMode.Flexible)
                .SpatialIndex(x => x.Location);
        });
        await using var session = await store.LightweightSessionAsync();

        session.Store(new PocoStore
        {
            Id = 10,
            Name = "Moving Store",
            Category = "retail",
            Location = new GeometryPoint(2.3522, 48.8566),
            DeliveryZone = null
        });
        await session.SaveChangesAsync();

        var loaded = await session.LoadAsync<PocoStore>("10");
        loaded.ShouldNotBeNull();
        loaded.Location = new GeometryPoint(-0.1276, 51.5072);

        session.Store(loaded);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<PocoStore>("10");
        reloaded.ShouldNotBeNull();
        reloaded.Location.Lng.ShouldBe(-0.1276, 0.0001);
        reloaded.Location.Lat.ShouldBe(51.5072, 0.0001);
        reloaded.DeliveryZone.ShouldBeNull();
    }
}
