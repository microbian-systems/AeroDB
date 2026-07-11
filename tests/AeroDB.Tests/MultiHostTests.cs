using AeroDB.Sable;

namespace AeroDB.Tests;

public class MultiHostTests
{
    [Test]
    public void MultiHost_configures_endpoints()
    {
        var options = new StoreOptions();
        options.AddDatabaseEndpoint("http://primary:8000", ep =>
        {
            ep.AcceptsWrites = true;
            ep.AcceptsReads = true;
            ep.Priority = 0;
        });
        options.AddDatabaseEndpoint("http://secondary:8000", ep =>
        {
            ep.AcceptsWrites = false;
            ep.AcceptsReads = true;
            ep.Priority = 1;
        });

        options.DatabaseEndpoints.Count.ShouldBe(2);
        options.DatabaseEndpoints[0].AcceptsWrites.ShouldBeTrue();
        options.DatabaseEndpoints[1].AcceptsWrites.ShouldBeFalse();
    }

    [Test]
    public void ReadPreference_default_is_primary()
    {
        var options = new StoreOptions();
        options.ReadPreference.ShouldBe(ReadPreference.Primary);
    }
}
