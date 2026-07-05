using TUnit.Core;

namespace AeroDB.Tests;

public class EntityQueryableExtensionTests
{
    [Test]
    public async Task IncludeReverse_compiles_for_Entity_types()
    {
        // This test verifies that IncludeReverse<T, TChild> compiles
        // when T is an Entity type (relaxed constraint).
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.QuerySessionAsync();

        // Build a query with IncludeReverse — just verifying it compiles and returns a queryable
        var query = session.Query<EntityIncludeParent>()
            .IncludeReverse<EntityIncludeParent, EntityIncludeChild>(
                p => p.Children,
                "ParentId");

        query.ShouldNotBeNull();
    }
}
