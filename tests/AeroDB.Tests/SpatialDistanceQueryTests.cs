using AeroDB.Sable;
using AeroDB.Sable.Internals.Cbor;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

public sealed class SpatialDistanceMappedStore
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public bool Deleted { get; set; }
    public GeometryPoint Location { get; set; } = new();
}

/// <summary>Regression coverage for the additive, parameterized distance materialization path.</summary>
public class SpatialDistanceQueryTests
{
    private static SurrealDbResponse EmptyResponse() => new([]);

    [Test]
    public async Task ToListWithDistanceAsync_uses_mapped_table_fields_parameters_and_schema_session()
    {
        var root = Substitute.For<ISurrealDbSession>();
        var routed = Substitute.For<ISurrealDbSession>();
        root.ForkSession(Arg.Any<CancellationToken>()).Returns(Task.FromResult(routed));
        routed.Use("geo-ns", "geo-db", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        routed.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(EmptyResponse()));

        var options = new StoreOptions { Namespace = "geo-ns" };
        options.Schema.For<SpatialDistanceMappedStore>()
            .TableName("mapped_store")
            .Schema("geo-db")
            .FieldName(store => store.Location, "location")
            .FieldName(store => store.TenantId, "tenant_id")
            .FieldName(store => store.Deleted, "is_deleted");

        await using var session = new QuerySession(Substitute.For<ISurrealDbClient>(), root, options, DocumentTracking.None);
        session.SetTenant("tenant-a");
        var query = session.Spatial<SpatialDistanceMappedStore>()
            .NearBy(store => store.Location, 48.8566, 2.3522, 1_000)
            .Skip(4)
            .Take(7);

        await Should.ThrowAsync<InvalidDataException>(() => query.ToListWithDistanceAsync());

        await routed.Received(1).RawQuery(
            Arg.Is<string>(sql => sql.Contains("FROM `mapped_store`")
                && sql.Contains("location")
                && sql.Contains("tenant_id")
                && sql.Contains("ORDER BY _distance ASC, id ASC")
                && sql.Contains("LIMIT $p")
                && sql.Contains("START AT $p")),
            Arg.Is<IReadOnlyDictionary<string, object?>>(parameters =>
                parameters.Values.OfType<GeometryPoint>().Any(point => point.Lng == 2.3522 && point.Lat == 48.8566)
                && parameters.Values.OfType<double>().Contains(1_000d)
                && parameters.Values.OfType<int>().Contains(7)
                && parameters.Values.OfType<int>().Contains(4)
                && parameters.Values.Contains("tenant-a")),
            Arg.Any<CancellationToken>());
        await root.DidNotReceiveWithAnyArgs().RawQuery(default!, default!, default);
    }

    [Test]
    public async Task ToListWithDistanceAsync_parameterizes_polygon_and_replaces_previous_mode()
    {
        var session = Substitute.For<ISurrealDbSession>();
        session.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(EmptyResponse()));
        var provider = new SurrealQueryProvider(session, new StoreOptions());
        var query = new AeroDBSpatialQuery<SpatialDistanceMappedStore>(provider);
        var polygon = new List<(double Lng, double Lat)> { (0, 0), (1, 0), (1, 1), (0, 1), (0, 0) };

        query.NearBy(store => store.Location, 0, 0, 10)
            .Within(store => store.Location, polygon);

        await Should.ThrowAsync<InvalidOperationException>(() => query.ToListWithDistanceAsync());
        await session.DidNotReceiveWithAnyArgs().RawQuery(default!, default!, default);

        query.OrderByDistance(store => store.Location, 12.5, -45.25);
        await Should.ThrowAsync<InvalidDataException>(() => query.ToListWithDistanceAsync());
        await session.Received(1).RawQuery(
            Arg.Is<string>(sql => sql.Contains("ORDER BY _distance ASC, id ASC") && !sql.Contains(" INSIDE ")),
            Arg.Is<IReadOnlyDictionary<string, object?>>(parameters => parameters.Values.OfType<GeometryPoint>()
                .Any(point => point.Lng == -45.25 && point.Lat == 12.5)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ToListWithDistanceAsync_honors_cancellation_before_dispatch()
    {
        var session = Substitute.For<ISurrealDbSession>();
        var query = new AeroDBSpatialQuery<SpatialDistanceMappedStore>(new SurrealQueryProvider(session, new StoreOptions()))
            .OrderByDistance(store => store.Location, 0, 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => query.ToListWithDistanceAsync(cancellation.Token));
        await session.DidNotReceiveWithAnyArgs().RawQuery(default!, default!, default);
    }

    [Test]
    public async Task Strict_distance_response_rejects_missing_malformed_and_invalid_distances()
    {
        var emptyStatus = CborResultReader.ReadPocoResultStrict(ReadOnlyMemory<byte>.Empty, out _);
        emptyStatus.ShouldBe(CborResultReader.StrictPocoResultStatus.Unreadable);

        var malformedStatus = CborResultReader.ReadPocoResultStrict(new byte[] { 0x01 }, out _);
        malformedStatus.ShouldBe(CborResultReader.StrictPocoResultStatus.Unreadable);

        var missing = new List<Dictionary<string, object?>> { new() };
        Should.Throw<InvalidDataException>(() => AeroDBSpatialQuery<SpatialDistanceMappedStore>.ValidateDistanceRows(missing, true));

        foreach (var invalid in new object?[] { -0.01d, double.NaN, double.PositiveInfinity, "12" })
        {
            var rows = new List<Dictionary<string, object?>> { new() { ["_distance"] = invalid } };
            Should.Throw<InvalidDataException>(() => AeroDBSpatialQuery<SpatialDistanceMappedStore>.ValidateDistanceRows(rows, true));
        }

        AeroDBSpatialQuery<SpatialDistanceMappedStore>.ValidateDistanceRows(
            [new Dictionary<string, object?> { ["_distance"] = 12.5m }], true)[0].ShouldBe(12.5d);
    }

    [Test]
    public async Task Bbox_prefilters_are_safe_at_poles_and_antimeridian()
    {
        var pole = SpatialBbox.ComputeSpherical(89.9, 0, 100_000);
        pole.CoversAllLongitudes.ShouldBeTrue();
        pole.CreatePrefilters("location", new SurrealCommandBuilder()).ShouldBeEmpty();

        var antimeridian = SpatialBbox.ComputeSpherical(0, 179.9, 100_000);
        antimeridian.CrossesAntimeridian.ShouldBeTrue();
        antimeridian.CreatePrefilters("location", new SurrealCommandBuilder()).Single().ShouldContain(" OR ");
    }
}
