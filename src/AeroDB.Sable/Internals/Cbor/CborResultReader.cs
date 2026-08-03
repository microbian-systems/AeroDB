using System.Runtime.CompilerServices;
using System.Globalization;
using Dahomey.Cbor;
using Dahomey.Cbor.Serialization;
using SurrealDb.Net.Models.Response;

namespace AeroDB.Sable.Internals.Cbor;

/// <summary>
/// Reads raw CBOR data directly from a SurrealDbOkResult without going through
/// Dahomey.Cbor's converter system. This avoids the issue where Dahomey.Cbor's
/// built-in ObjectConverter cannot handle CBOR maps.
/// </summary>
internal static class CborResultReader
{
    private const ulong RecordIdTag = 8;
    private const ulong CustomDateTimeTag = 12;
    private const ulong UuidTag = 37;
    private const ulong GeometryPointTag = 88;
    private const ulong GeometryLineTag = 89;
    private const ulong GeometryPolygonTag = 90;

    internal enum StrictPocoResultStatus
    {
        EmptyProviderResponse,
        EmptyRows,
        Rows,
        Unreadable
    }

    /// <summary>
    /// Strictly reads one SELECT result without conflating an empty successful array
    /// with a missing or malformed provider response.
    /// </summary>
    internal static StrictPocoResultStatus ReadPocoResultStrict(
        SurrealDbResponse response,
        int index,
        out List<Dictionary<string, object?>> records)
    {
        records = [];
        if (response.Count == 0)
            return StrictPocoResultStatus.EmptyProviderResponse;
        if (!TryGetBinaryResult(response, index, out var mem))
            return StrictPocoResultStatus.Unreadable;

        return ReadPocoResultStrict(mem, out records);
    }

    /// <summary>
    /// Strictly decodes one raw SELECT-result CBOR buffer. This seam keeps
    /// malformed-row handling testable without constructing provider internals.
    /// </summary>
    internal static StrictPocoResultStatus ReadPocoResultStrict(
        ReadOnlyMemory<byte> cbor,
        out List<Dictionary<string, object?>> records)
    {
        records = [];
        if (cbor.IsEmpty)
            return StrictPocoResultStatus.Unreadable;

        try
        {
            var reader = new CborReader(cbor.Span);
            if (reader.GetCurrentDataItemType() != CborDataItemType.Array)
                return StrictPocoResultStatus.Unreadable;

            reader.ReadBeginArray();
            var size = reader.ReadSize();
            if (size == 0)
                return StrictPocoResultStatus.EmptyRows;

            records = new List<Dictionary<string, object?>>(size);
            for (var i = 0; i < size; i++)
            {
                var row = ReadCborMapIntoDictionary(ref reader);
                if (row is null)
                {
                    records = [];
                    return StrictPocoResultStatus.Unreadable;
                }
                records.Add(row);
            }
            return StrictPocoResultStatus.Rows;
        }
        catch (CborException)
        {
            records = [];
            return StrictPocoResultStatus.Unreadable;
        }
    }

    /// <summary>
    /// Reads the raw CBOR result at the given index and returns it as a list of dictionaries.
    /// </summary>
    internal static List<Dictionary<string, object?>> ReadPocoResult(SurrealDbResponse response, int index)
    {
        if (!TryGetBinaryResult(response, index, out var mem))
            return [];

        var reader = new CborReader(mem.Span);
        return ReadCborArrayIntoList(ref reader);
    }

    /// <summary>
    /// Reads a top-level CBOR array whose elements are arrays of record maps.
    /// This is the shape produced by <c>SELECT VALUE ->edge->target.* FROM source</c>.
    /// </summary>
    internal static List<List<Dictionary<string, object?>>> ReadPocoNestedResult(
        SurrealDbResponse response,
        int index)
    {
        if (!TryGetBinaryResult(response, index, out var mem))
            return [];

        var reader = new CborReader(mem.Span);
        return ReadCborArrayIntoNestedList(ref reader);
    }

    private static bool TryGetBinaryResult(
        SurrealDbResponse response,
        int index,
        out ReadOnlyMemory<byte> binaryResult)
    {
        binaryResult = default;

        if (response.Count <= index || response[index] is not SurrealDbOkResult okResult)
            return false;

        var binaryField = typeof(SurrealDbOkResult).GetField(
            "_binaryResult",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        if (binaryField is null)
            return false;

        var value = (ReadOnlyMemory<byte>?)(binaryField.GetValue(okResult));
        if (value is not { } mem || mem.IsEmpty)
            return false;

        binaryResult = mem;
        return true;
    }

    private static List<Dictionary<string, object?>> ReadCborArrayIntoList(ref CborReader reader)
    {
        if (reader.GetCurrentDataItemType() != CborDataItemType.Array)
        {
            reader.SkipDataItem();
            return [];
        }

        reader.ReadBeginArray();
        int size = reader.ReadSize();
        var list = new List<Dictionary<string, object?>>(size);

        for (int i = 0; i < size; i++)
        {
            var dict = ReadCborMapIntoDictionary(ref reader);
            if (dict is not null)
                list.Add(dict);
        }

        return list;
    }

    private static List<List<Dictionary<string, object?>>> ReadCborArrayIntoNestedList(ref CborReader reader)
    {
        if (reader.GetCurrentDataItemType() != CborDataItemType.Array)
        {
            reader.SkipDataItem();
            return [];
        }

        reader.ReadBeginArray();
        var size = reader.ReadSize();
        var list = new List<List<Dictionary<string, object?>>>(size);

        for (var i = 0; i < size; i++)
        {
            list.Add(ReadCborNestedItemIntoList(ref reader));
        }

        return list;
    }

    private static List<Dictionary<string, object?>> ReadCborNestedItemIntoList(ref CborReader reader)
    {
        if (reader.GetCurrentDataItemType() == CborDataItemType.Array)
        {
            reader.ReadBeginArray();
            var size = reader.ReadSize();
            var list = new List<Dictionary<string, object?>>(size);

            for (var i = 0; i < size; i++)
            {
                var dict = ReadCborMapIntoDictionary(ref reader);
                if (dict is not null)
                    list.Add(dict);
            }

            return list;
        }

        var single = ReadCborMapIntoDictionary(ref reader);
        return single is null ? [] : [single];
    }

    private static Dictionary<string, object?>? ReadCborMapIntoDictionary(ref CborReader reader)
    {
        if (reader.GetCurrentDataItemType() == CborDataItemType.Null)
        {
            reader.ReadNull();
            return null;
        }

        if (reader.GetCurrentDataItemType() != CborDataItemType.Map)
        {
            reader.SkipDataItem();
            return null;
        }

        reader.ReadBeginMap();
        int remainingItemCount = reader.ReadSize();
        var dict = new Dictionary<string, object?>(remainingItemCount);

        while (reader.MoveNextMapItem(ref remainingItemCount))
        {
            string? key = reader.ReadString();
            if (key is not null)
            {
                object? value = ReadCborValueIntoObject(ref reader);
                dict[key] = value;
            }
            else
            {
                reader.SkipDataItem();
            }
        }

        return dict;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object? ReadCborValueIntoObject(ref CborReader reader)
    {
        if (reader.TryReadSemanticTag(out var semanticTag))
        {
            return ReadTaggedValueIntoObject(ref reader, semanticTag);
        }

        var itemType = reader.GetCurrentDataItemType();

        return itemType switch
        {
            CborDataItemType.Null => ReadNullIntoObject(ref reader),
            CborDataItemType.Boolean => reader.ReadBoolean(),
            CborDataItemType.String => reader.ReadString(),
            CborDataItemType.Signed => reader.ReadInt64(),
            CborDataItemType.Unsigned => reader.ReadUInt64(),
            CborDataItemType.Single => reader.ReadSingle(),
            CborDataItemType.Double => reader.ReadDouble(),
            CborDataItemType.Map => ReadCborMapIntoDictionary(ref reader),
            CborDataItemType.Array => ReadCborArrayValuesIntoList(ref reader),
            _ => SkipAndReturnNull(ref reader),
        };
    }

    private static object? ReadNullIntoObject(ref CborReader reader)
    {
        reader.ReadNull();
        return null;
    }

    private static object? ReadTaggedValueIntoObject(ref CborReader reader, ulong semanticTag)
    {
        return semanticTag switch
        {
            RecordIdTag => ReadRecordIdIntoString(ref reader),
            CustomDateTimeTag => ReadDateTimeIntoObject(ref reader),
            UuidTag => ReadGuidIntoObject(ref reader),
            GeometryPointTag => ReadGeometryPointIntoObject(ref reader),
            GeometryPolygonTag => ReadGeometryPolygonIntoObject(ref reader),
            GeometryLineTag => ReadGeometryLineIntoObject(ref reader),
            _ => ReadCborValueIntoObject(ref reader)
        };
    }

    private static string? ReadRecordIdIntoString(ref CborReader reader)
    {
        if (reader.GetCurrentDataItemType() != CborDataItemType.Array)
        {
            reader.SkipDataItem();
            return null;
        }

        reader.ReadBeginArray();
        var size = reader.ReadSize();
        if (size != 2)
        {
            for (var i = 0; i < size; i++)
            {
                reader.SkipDataItem();
            }

            return null;
        }

        var table = reader.ReadString();
        var id = ReadCborValueIntoObject(ref reader);
        if (string.IsNullOrEmpty(table))
        {
            return FormatRecordIdPart(id);
        }

        var idPart = FormatRecordIdPart(id);
        return idPart is null ? table : $"{table}:{idPart}";
    }

    private static DateTime ReadDateTimeIntoObject(ref CborReader reader)
    {
        reader.ReadBeginArray();

        var size = reader.ReadSize();
        var seconds = size >= 1 ? reader.ReadInt64() : 0;
        var nanos = size >= 2 ? reader.ReadInt32() : 0;
        for (var i = 2; i < size; i++)
        {
            reader.SkipDataItem();
        }

        return DateTime.UnixEpoch
            .AddSeconds(seconds)
            .AddTicks((long)Math.Round(nanos / 100d));
    }

    private static Guid? ReadGuidIntoObject(ref CborReader reader)
    {
        var value = reader.ReadByteString();
        return value.Length == 16 ? new Guid(value, true) : null;
    }

    private static GeometryPoint? ReadGeometryPointIntoObject(ref CborReader reader)
    {
        if (reader.GetCurrentDataItemType() == CborDataItemType.Null)
        {
            reader.ReadNull();
            return null;
        }

        if (reader.GetCurrentDataItemType() != CborDataItemType.Array)
        {
            reader.SkipDataItem();
            return null;
        }

        reader.ReadBeginArray();
        var size = reader.ReadSize();
        if (size != 2)
        {
            for (var i = 0; i < size; i++)
                reader.SkipDataItem();

            return null;
        }

        return new GeometryPoint(reader.ReadDouble(), reader.ReadDouble());
    }

    private static GeometryPolygon? ReadGeometryPolygonIntoObject(ref CborReader reader)
    {
        if (reader.GetCurrentDataItemType() == CborDataItemType.Null)
        {
            reader.ReadNull();
            return null;
        }

        if (reader.GetCurrentDataItemType() != CborDataItemType.Array)
        {
            reader.SkipDataItem();
            return null;
        }

        reader.ReadBeginArray();
        var size = reader.ReadSize();
        var rings = new List<List<(double Lng, double Lat)>>(size);

        for (var i = 0; i < size; i++)
        {
            var line = ReadCborValueIntoObject(ref reader);
            if (line is List<object?> values)
            {
                var ring = new List<(double Lng, double Lat)>(values.Count);
                foreach (var value in values)
                {
                    if (value is GeometryPoint point)
                        ring.Add((point.Lng, point.Lat));
                }

                rings.Add(ring);
            }
        }

        return new GeometryPolygon { Rings = rings };
    }

    private static List<object?> ReadGeometryLineIntoObject(ref CborReader reader)
        => ReadCborArrayValuesIntoList(ref reader);

    private static string? FormatRecordIdPart(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static List<object?> ReadCborArrayValuesIntoList(ref CborReader reader)
    {
        reader.ReadBeginArray();
        int size = reader.ReadSize();
        var list = new List<object?>(size);
        for (int i = 0; i < size; i++)
            list.Add(ReadCborValueIntoObject(ref reader));
        return list;
    }

    private static object? SkipAndReturnNull(ref CborReader reader)
    {
        reader.SkipDataItem();
        return null;
    }
}
