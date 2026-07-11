using AeroDB.SourceGenerators;

namespace AeroDB.Tests;

using Shouldly;
using TUnit.Core;
using WolverineFx;

public class AeroDBTransportOptionsTests
{
    [Test]
    public void DefaultValues_AreReasonable()
    {
        var options = new AeroDBTransportOptions();
        options.PollingInterval.ShouldBe(TimeSpan.FromSeconds(5));
        options.BatchSize.ShouldBe(100);
        options.UseLiveQuery.ShouldBeFalse();
    }

    [Test]
    public void CanCustomize()
    {
        var options = new AeroDBTransportOptions
        {
            PollingInterval = TimeSpan.FromSeconds(10),
            BatchSize = 50,
            UseLiveQuery = true
        };
        options.PollingInterval.TotalSeconds.ShouldBe(10);
        options.BatchSize.ShouldBe(50);
        options.UseLiveQuery.ShouldBeTrue();
    }

    [Test]
    public void PollingInterval_CanBeSetToZero()
    {
        var options = new AeroDBTransportOptions
        {
            PollingInterval = TimeSpan.Zero
        };
        options.PollingInterval.ShouldBe(TimeSpan.Zero);
    }

    [Test]
    public void BatchSize_CanBeSetToOne()
    {
        var options = new AeroDBTransportOptions
        {
            BatchSize = 1
        };
        options.BatchSize.ShouldBe(1);
    }

    [Test]
    public void UseLiveQuery_DefaultsToFalse()
    {
        var options = new AeroDBTransportOptions();
        options.UseLiveQuery.ShouldBeFalse();
    }
}
