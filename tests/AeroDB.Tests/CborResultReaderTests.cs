using AeroDB.Sable.Internals.Cbor;
using Shouldly;

namespace AeroDB.Tests;

public sealed class CborResultReaderTests
{
    [Test]
    public void Null_map_values_are_materialized_as_null()
    {
        // [{ "value": null }]
        ReadOnlyMemory<byte> cbor = new byte[]
        {
            0x81,
            0xA1,
            0x65, 0x76, 0x61, 0x6C, 0x75, 0x65,
            0xF6
        };

        var status = CborResultReader.ReadPocoResultStrict(cbor, out var records);

        status.ShouldBe(CborResultReader.StrictPocoResultStatus.Rows);
        records.ShouldHaveSingleItem();
        records[0].ShouldContainKey("value");
        records[0]["value"].ShouldBeNull();
    }
}
