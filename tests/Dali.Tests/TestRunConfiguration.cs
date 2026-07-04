using TUnit.Core;

namespace Dali.Tests;

public static class TestRunConfiguration
{
    [Before(HookType.TestDiscovery)]
    public static void Configure(BeforeTestDiscoveryContext context)
    {
        // SurrealDB embedded can complete native callbacks twice under heavy parallel test load.
        // Keep the default local run stable; CLI/env settings can still override this.
        context.Settings.Parallelism.MaximumParallelTests = 1;
    }
}
