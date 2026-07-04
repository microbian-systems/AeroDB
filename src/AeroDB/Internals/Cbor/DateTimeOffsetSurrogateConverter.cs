using Dahomey.Cbor;
using Dahomey.Cbor.Serialization;
using Dahomey.Cbor.Serialization.Converters;

namespace AeroDB.Internals.Cbor;

internal sealed class DateTimeOffsetSurrogateConverter : CborConverterBase<DateTimeOffset>
{
    private const int CustomDateTimeTag = 12;
    private const int NanosecondsPerTick = 100;
    private const int NanosecondsPerMicrosecond = 1_000;
    private const int NanosecondsPerMillisecond = 1_000_000;

    public override DateTimeOffset Read(ref CborReader reader)
    {
        reader.ReadBeginArray();

        var size = reader.ReadSize();
        if (size > 2)
        {
            throw new CborException("Expected a CBOR array with at most 2 elements");
        }

        var seconds = size >= 1 ? reader.ReadInt64() : 0;
        var nanos = size >= 2 ? reader.ReadInt32() : 0;

        var value = DateTime.UnixEpoch
            .AddSeconds(seconds)
            .AddTicks((long)Math.Round((double)nanos / NanosecondsPerTick));

        return new DateTimeOffset(value);
    }

    public override void Write(ref CborWriter writer, DateTimeOffset value)
    {
        writer.WriteSemanticTag(CustomDateTimeTag);

        var (seconds, nanos) = Convert(value.UtcDateTime);

        writer.WriteBeginArray(2);
        writer.WriteInt64(seconds);
        writer.WriteInt32(nanos);
        writer.WriteEndArray(2);
    }

    private static (long Seconds, int Nanos) Convert(DateTime value)
    {
        var utcValue = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        var diff = utcValue - DateTime.UnixEpoch;

        var seconds = diff.Ticks / TimeSpan.TicksPerSecond;
        var nanos = utcValue.Nanosecond
            + (utcValue.Microsecond * NanosecondsPerMicrosecond)
            + (utcValue.Millisecond * NanosecondsPerMillisecond);

        if (nanos > 0 && seconds < 0)
        {
            seconds--;
        }

        return (seconds, nanos);
    }
}
