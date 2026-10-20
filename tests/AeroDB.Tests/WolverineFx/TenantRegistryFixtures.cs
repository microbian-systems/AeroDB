using AeroDB.WolverineFx;

namespace AeroDB.Tests;

internal static class TenantRegistryFixtures
{
    // Low-level registry/cache fixtures have no resource-composition source.
    // This shortcut deliberately exists only in the test assembly.
    internal static void ActivateForTest(this AeroDBTenantStoreRegistry registry,
        IEnumerable<AeroDBTenantStoreLocation> locations) => registry.Activate(registry.Stage(locations));
}
