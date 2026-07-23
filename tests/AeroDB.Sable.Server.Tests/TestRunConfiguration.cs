namespace AeroDB.Sable.Server.Tests;

public static class TestRunConfiguration
{
    [Before(HookType.TestDiscovery)]
    public static void Configure(BeforeTestDiscoveryContext context)
    {
        // The capability scenarios use stable database names so failures remain
        // inspectable. Serialize them to prevent one scenario resetting another.
        context.Settings.Parallelism.MaximumParallelTests = 1;
    }
}
