using System.Buffers;
using System.Text.Json;
using Dahomey.Cbor;
using Dahomey.Cbor.Serialization;
using Dahomey.Cbor.Serialization.Converters;

namespace AeroDB.Sable.Internals.Cbor;

/// <summary>
/// Dahomey.Cbor converter for <see cref="JsonElement"/>.
///
/// <b>Write</b>: encodes <see cref="JsonElement"/> values directly as proper CBOR
/// types (maps, arrays, strings, numbers, booleans, null) instead of reflecting on
/// the C# struct internals.
/// </summary>
internal sealed class JsonElementCborConverter : CborConverterBase<JsonElement>
{
    // ── Write ──────────────────────────────────────────────────────────

    public override void Write(ref CborWriter writer, JsonElement value)
    {
        WriteValue(ref writer, value);
    }

    private static void WriteValue(ref CborWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteBeginMap(-1);
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WriteString(prop.Name);
                    WriteValue(ref writer, prop.Value);
                }
                writer.WriteEndMap(-1);
                break;

            case JsonValueKind.Array:
                writer.WriteBeginArray(-1);
                foreach (var item in element.EnumerateArray())
                    WriteValue(ref writer, item);
                writer.WriteEndArray(-1);
                break;

            case JsonValueKind.String:
                writer.WriteString(element.GetString()!);
                break;

            case JsonValueKind.Number:
                WriteNumber(ref writer, element);
                break;

            case JsonValueKind.True:
                writer.WriteBoolean(true);
                break;

            case JsonValueKind.False:
                writer.WriteBoolean(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNull();
                break;

            default:
                writer.WriteNull();
                break;
        }
    }

    private static void WriteNumber(ref CborWriter writer, JsonElement element)
    {
        var raw = element.GetRawText();

        if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var i64))
        {
            writer.WriteInt64(i64);
        }
        else if (ulong.TryParse(raw, out var u64))
        {
            writer.WriteUInt64(u64);
        }
        else if (double.TryParse(raw, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                     System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            writer.WriteDouble(d);
        }
        else
        {
            writer.WriteString(raw);
        }
    }

    // ── Read (round-trip via JSON bytes) ───────────────────────────────

    public override JsonElement Read(ref CborReader reader)
    {
        var buffer = new PooledArrayBufferWriter<byte>();
        var jsonWriter = new Utf8JsonWriter(buffer);
        try
        {
            CborToJson(ref reader, jsonWriter);
            jsonWriter.Flush();

            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            return doc.RootElement.Clone();
        }
        finally
        {
            jsonWriter.Dispose();
            buffer.Dispose();
        }
    }

    private static void CborToJson(ref CborReader reader, Utf8JsonWriter w)
    {
        var type = reader.GetCurrentDataItemType();

        switch (type)
        {
            case CborDataItemType.Map:
                {
                    reader.ReadBeginMap();
                    var count = reader.ReadSize();
                    w.WriteStartObject();

                    for (var i = 0; i < count; i++)
                    {
                        var key = reader.ReadString();
                        w.WritePropertyName(key);
                        CborToJson(ref reader, w);
                    }

                    w.WriteEndObject();
                }
                break;

            case CborDataItemType.Array:
                {
                    reader.ReadBeginArray();
                    var count = reader.ReadSize();
                    w.WriteStartArray();

                    for (var i = 0; i < count; i++)
                        CborToJson(ref reader, w);

                    w.WriteEndArray();
                }
                break;

            case CborDataItemType.String:
                w.WriteStringValue(reader.ReadString());
                break;

            case CborDataItemType.Signed:
                w.WriteNumberValue(reader.ReadInt64());
                break;

            case CborDataItemType.Unsigned:
                w.WriteNumberValue(reader.ReadUInt64());
                break;

            case CborDataItemType.Single:
                w.WriteNumberValue(reader.ReadSingle());
                break;

            case CborDataItemType.Double:
                w.WriteNumberValue(reader.ReadDouble());
                break;

            case CborDataItemType.Boolean:
                w.WriteBooleanValue(reader.ReadBoolean());
                break;

            case CborDataItemType.Null:
                reader.ReadNull();
                w.WriteNullValue();
                break;

            default:
                reader.SkipDataItem();
                w.WriteNullValue();
                break;
        }
    }
}

/// <summary>
/// Minimal pooled <see cref="IBufferWriter{T}"/> backed by <see cref="ArrayPool{T}"/>.
/// Used for temporary JSON byte buffers during CBOR→JsonElement deserialization.
/// </summary>
internal sealed class PooledArrayBufferWriter<T> : IBufferWriter<T>, IDisposable
{
    private T[] _buffer;
    private int _index;

    public PooledArrayBufferWriter(int initialCapacity = 1024)
    {
        _buffer = ArrayPool<T>.Shared.Rent(initialCapacity);
    }

    public ReadOnlyMemory<T> WrittenMemory => _buffer.AsMemory(0, _index);

    public void Advance(int count)
    {
        _index += count;
    }

    public Memory<T> GetMemory(int sizeHint = 0)
    {
        var needed = _index + Math.Max(1, sizeHint);
        if (needed > _buffer.Length)
            Resize(needed);
        return _buffer.AsMemory(_index);
    }

    public Span<T> GetSpan(int sizeHint = 0)
    {
        var needed = _index + Math.Max(1, sizeHint);
        if (needed > _buffer.Length)
            Resize(needed);
        return _buffer.AsSpan(_index);
    }

    private void Resize(int minSize)
    {
        var newSize = Math.Max(_buffer.Length * 2, minSize);
        var newBuffer = ArrayPool<T>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _index).CopyTo(newBuffer);
        ArrayPool<T>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    public void Dispose()
    {
        if (_buffer is { Length: > 0 })
        {
            ArrayPool<T>.Shared.Return(_buffer);
            _buffer = Array.Empty<T>();
        }
    }
}
