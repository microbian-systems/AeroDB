using AeroDB.Sable;
using Shouldly;

namespace AeroDB.Tests;

public sealed class PocoEnumMaterializationTests
{
    [Test]
    public void DeserializePocoFromList_materializes_string_enum_values_during_normalization()
    {
        var records = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = "Published"
            }
        };

        var result = InternalSessionBase.DeserializePocoFromList<EnumProjection>(
            records,
            enumStorage: EnumStorage.AsString);

        result.Single().Status.ShouldBe(MaterializationStatus.Published);
    }

    [Test]
    public void DeserializePocoFromList_materializes_integer_enum_values_during_normalization()
    {
        var records = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = 2L
            }
        };

        var result = InternalSessionBase.DeserializePocoFromList<EnumProjection>(
            records,
            enumStorage: EnumStorage.AsInteger);

        result.Single().Status.ShouldBe(MaterializationStatus.Archived);
    }

    private sealed class EnumProjection
    {
        public MaterializationStatus Status { get; set; }
    }

    private enum MaterializationStatus
    {
        Draft,
        Published,
        Archived
    }
}
