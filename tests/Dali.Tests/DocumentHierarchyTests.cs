using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

public class DocumentHierarchyTests
{
    [Test]
    public void Hierarchy_registers_subtypes()
    {
        var hierarchy = new DocumentHierarchy(typeof(BaseDoc));
        hierarchy.AddSubClass<DerivedDoc>();
        hierarchy.SubTypes.Count.ShouldBe(1);
        hierarchy.SubTypes.ShouldContain(typeof(DerivedDoc));
    }

    [Test]
    public void StoreOptions_HierarchyFor_returns_same_instance()
    {
        var options = new StoreOptions();
        var h1 = options.HierarchyFor<BaseDoc>();
        var h2 = options.HierarchyFor<BaseDoc>();
        h1.ShouldBeSameAs(h2);
    }
}

internal class BaseDoc : Record
{
    public string Name { get; set; } = "";
}

internal class DerivedDoc : BaseDoc
{
    public string Extra { get; set; } = "";
}
