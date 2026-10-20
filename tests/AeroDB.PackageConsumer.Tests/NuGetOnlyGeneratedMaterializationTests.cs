using AeroDB.Sable;
using AeroDB.Sable.Metadata;
using Shouldly;
using TUnit.Core;

namespace AeroDB.PackageConsumer.Tests;

public sealed class NuGetOnlyGeneratedMaterializationTests
{
    [Test]
    public void NuGet_only_consumer_registers_and_uses_the_generated_materializer()
    {
        var values = new Dictionary<string, object?>
        {
            ["id"] = "package_consumer_document:42",
            ["solicitation_number"] = "FAKE-TEST-42",
            ["required_technicians"] = 3
        };

        var materialized = MetadataRegistry.TryMaterialize<PackageConsumerDocument>(
            values,
            schema: null,
            EnumStorage.AsString,
            out var document);

        materialized.ShouldBeTrue();
        document.Id.ShouldBe(42L);
        document.SolicitationNumber.ShouldBe("FAKE-TEST-42");
        document.RequiredTechnicians.ShouldBe(3);
    }
}
