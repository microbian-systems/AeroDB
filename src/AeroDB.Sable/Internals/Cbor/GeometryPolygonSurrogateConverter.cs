using Dahomey.Cbor;
using Dahomey.Cbor.Serialization;
using Dahomey.Cbor.Serialization.Converters;

namespace AeroDB.Sable.Internals.Cbor;

internal sealed class GeometryPolygonSurrogateConverter : CborConverterBase<GeometryPolygon>
{
    internal const ulong GeometryLineTag = 89;
    internal const ulong GeometryPolygonTag = 90;

    public override GeometryPolygon Read(ref CborReader reader)
    {
        if (reader.GetCurrentDataItemType() == CborDataItemType.Null)
        {
            reader.ReadNull();
            return new GeometryPolygon();
        }

        reader.ReadBeginArray();
        var size = reader.ReadSize();
        var rings = new List<List<(double Lng, double Lat)>>(size);

        for (var i = 0; i < size; i++)
            rings.Add(ReadRing(ref reader));

        return new GeometryPolygon { Rings = rings };
    }

    public override void Write(ref CborWriter writer, GeometryPolygon value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteBeginMap(2);
        writer.WriteString("type");
        writer.WriteString("Polygon");
        writer.WriteString("coordinates");
        writer.WriteBeginArray(value.Rings.Count);
        foreach (var ring in value.Rings)
        {
            writer.WriteBeginArray(ring.Count);

            foreach (var point in ring)
            {
                writer.WriteBeginArray(2);
                writer.WriteDouble(point.Lng);
                writer.WriteDouble(point.Lat);
                writer.WriteEndArray(2);
            }

            writer.WriteEndArray(ring.Count);
        }

        writer.WriteEndArray(value.Rings.Count);
        writer.WriteEndMap(2);
    }

    private static List<(double Lng, double Lat)> ReadRing(ref CborReader reader)
    {
        reader.TryReadSemanticTag(out _);
        reader.ReadBeginArray();
        var size = reader.ReadSize();
        var ring = new List<(double Lng, double Lat)>(size);

        for (var i = 0; i < size; i++)
        {
            reader.TryReadSemanticTag(out _);
            reader.ReadBeginArray();
            var pointSize = reader.ReadSize();
            if (pointSize != 2)
            {
                for (var j = 0; j < pointSize; j++)
                    reader.SkipDataItem();

                continue;
            }

            ring.Add((reader.ReadDouble(), reader.ReadDouble()));
        }

        return ring;
    }
}
