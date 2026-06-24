namespace Dali.Tests;

using Shouldly;
using TUnit.Core;
using WolverineFx;

public class DaliTransportOptionsTests
{
    [Test]
    public void DefaultValues_AreReasonable()
    {
        var options = new DaliTransportOptions();
        options.PollingInterval.ShouldBe(TimeSpan.FromSeconds(5));
        options.BatchSize.ShouldBe(100);
        options.UseLiveQuery.ShouldBeFalse();
    }

    [Test]
    public void CanCustomize()
    {
        var options = new DaliTransportOptions
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
        var options = new DaliTransportOptions
        {
            PollingInterval = TimeSpan.Zero
        };
        options.PollingInterval.ShouldBe(TimeSpan.Zero);
    }

    [Test]
    public void BatchSize_CanBeSetToOne()
    {
        var options = new DaliTransportOptions
        {
            BatchSize = 1
        };
        options.BatchSize.ShouldBe(1);
    }

    [Test]
    public void UseLiveQuery_DefaultsToFalse()
    {
        var options = new DaliTransportOptions();
        options.UseLiveQuery.ShouldBeFalse();
    }
}
