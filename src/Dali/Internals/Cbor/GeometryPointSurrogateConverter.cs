using Dahomey.Cbor;
using Dahomey.Cbor.Serialization;
using Dahomey.Cbor.Serialization.Converters;

namespace Dali.Internals.Cbor;

internal sealed class GeometryPointSurrogateConverter : CborConverterBase<GeometryPoint>
{
    internal const ulong GeometryPointTag = 88;

    public override GeometryPoint Read(ref CborReader reader)
    {
        if (reader.GetCurrentDataItemType() == CborDataItemType.Null)
        {
            reader.ReadNull();
            return new GeometryPoint();
        }

        reader.ReadBeginArray();
        var size = reader.ReadSize();
        if (size != 2)
        {
            for (var i = 0; i < size; i++)
                reader.SkipDataItem();

            throw new CborException("Expected a CBOR array with 2 elements for GeometryPoint.");
        }

        var lng = reader.ReadDouble();
        var lat = reader.ReadDouble();
        return new GeometryPoint(lng, lat);
    }

    public override void Write(ref CborWriter writer, GeometryPoint value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteBeginMap(2);
        writer.WriteString("type");
        writer.WriteString("Point");
        writer.WriteString("coordinates");
        writer.WriteBeginArray(2);
        writer.WriteDouble(value.Lng);
        writer.WriteDouble(value.Lat);
        writer.WriteEndArray(2);
        writer.WriteEndMap(2);
    }
}
