using AeroDB.Sable;
using AeroDB.Sable.Internals.Cbor;
using Dahomey.Cbor;
using Dahomey.Cbor.Serialization;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>Locks the native SurrealDB CBOR geometry layout used by spatial parameters.</summary>
public class SpatialGeometryCborTests
{
    [Test]
    public async Task Point_uses_tag_88_and_round_trips()
    {
        var point = new GeometryPoint(2.3522, 48.8566);
        var bytes = await SerializeAsync(point);
        var reader = new CborReader(bytes);
        reader.TryReadSemanticTag(out var tag).ShouldBeTrue();
        tag.ShouldBe(88ul);
        reader.ReadBeginArray();
        reader.ReadSize().ShouldBe(2);
        reader.ReadDouble().ShouldBe(2.3522d);
        reader.ReadDouble().ShouldBe(48.8566d);

        await using var stream = new MemoryStream(bytes);
        var roundTrip = await Cbor.DeserializeAsync<GeometryPoint>(stream, AeroDBCborOptions.GetCborSerializerOptions());
        roundTrip.Lng.ShouldBe(point.Lng);
        roundTrip.Lat.ShouldBe(point.Lat);
    }

    [Test]
    public async Task Polygon_uses_polygon_ring_and_point_tags_and_round_trips()
    {
        var polygon = new GeometryPolygon(new List<(double Lng, double Lat)>
        {
            (0, 0), (1, 0), (1, 1), (0, 0)
        });
        var bytes = await SerializeAsync(polygon);
        var reader = new CborReader(bytes);
        reader.TryReadSemanticTag(out var polygonTag).ShouldBeTrue();
        polygonTag.ShouldBe(90ul);
        reader.ReadBeginArray();
        reader.ReadSize().ShouldBe(1);
        reader.TryReadSemanticTag(out var ringTag).ShouldBeTrue();
        ringTag.ShouldBe(89ul);
        reader.ReadBeginArray();
        reader.ReadSize().ShouldBe(4);
        reader.TryReadSemanticTag(out var pointTag).ShouldBeTrue();
        pointTag.ShouldBe(88ul);

        await using var stream = new MemoryStream(bytes);
        var roundTrip = await Cbor.DeserializeAsync<GeometryPolygon>(stream, AeroDBCborOptions.GetCborSerializerOptions());
        roundTrip.Rings.Single().ShouldBe(polygon.Rings.Single());
    }

    [Test]
    public async Task Tagged_null_geometries_preserve_the_native_tag()
    {
        var point = await SerializeAsync<GeometryPoint>(null!);
        var pointReader = new CborReader(point);
        pointReader.TryReadSemanticTag(out var pointTag).ShouldBeTrue();
        pointTag.ShouldBe(88ul);
        pointReader.GetCurrentDataItemType().ShouldBe(CborDataItemType.Null);

        var polygon = await SerializeAsync<GeometryPolygon>(null!);
        var polygonReader = new CborReader(polygon);
        polygonReader.TryReadSemanticTag(out var polygonTag).ShouldBeTrue();
        polygonTag.ShouldBe(90ul);
        polygonReader.GetCurrentDataItemType().ShouldBe(CborDataItemType.Null);
    }

    private static async Task<byte[]> SerializeAsync<T>(T value)
    {
        await using var stream = new MemoryStream();
        await Cbor.SerializeAsync(value, stream, AeroDBCborOptions.GetCborSerializerOptions());
        return stream.ToArray();
    }
}
