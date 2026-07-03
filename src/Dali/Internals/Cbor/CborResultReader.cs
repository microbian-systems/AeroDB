using System.Runtime.CompilerServices;
using System.Globalization;
using Dahomey.Cbor;
using Dahomey.Cbor.Serialization;
using SurrealDb.Net.Models.Response;

namespace Dali.Internals.Cbor;

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

    /// <summary>
    /// Reads the raw CBOR result at the given index and returns it as a list of dictionaries.
    /// </summary>
    internal static List<Dictionary<string, object?>> ReadPocoResult(SurrealDbResponse response, int index)
    {
        if (response.Count <= index)
            return [];

        if (response[index] is not SurrealDbOkResult okResult)
            return [];

        // Access the internal _binaryResult field via reflection
        var binaryField = typeof(SurrealDbOkResult).GetField(
            "_binaryResult",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        if (binaryField is null)
            return [];

        var binaryResult = (ReadOnlyMemory<byte>?)(binaryField.GetValue(okResult));
        if (binaryResult is not { } mem || mem.IsEmpty)
            return [];

        // Parse the CBOR data directly
        var reader = new CborReader(mem.Span);
        return ReadCborArrayIntoList(ref reader);
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
            CborDataItemType.Null => reader.ReadNull(),
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

    private static object? ReadTaggedValueIntoObject(ref CborReader reader, ulong semanticTag)
    {
        return semanticTag switch
        {
            RecordIdTag => ReadRecordIdIntoString(ref reader),
            CustomDateTimeTag => ReadDateTimeIntoObject(ref reader),
            UuidTag => ReadGuidIntoObject(ref reader),
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
        if (size > 2)
        {
            for (var i = 0; i < size; i++)
            {
                reader.SkipDataItem();
            }

            return DateTime.UnixEpoch;
        }

        var seconds = size >= 1 ? reader.ReadInt64() : 0;
        var nanos = size >= 2 ? reader.ReadInt32() : 0;

        return DateTime.UnixEpoch
            .AddSeconds(seconds)
            .AddTicks((long)Math.Round(nanos / 100d));
    }

    private static Guid? ReadGuidIntoObject(ref CborReader reader)
    {
        var value = reader.ReadByteString();
        return value.Length == 16 ? new Guid(value, true) : null;
    }

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
