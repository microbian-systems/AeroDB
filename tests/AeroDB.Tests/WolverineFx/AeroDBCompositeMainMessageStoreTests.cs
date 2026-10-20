using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace AeroDB.Tests;

public sealed class AeroDBCompositeMainMessageStoreTests
{
    [Test]
    public async Task Composite_main_wrapper_is_an_agent_store_without_disposing_its_borrowed_raw_store()
    {
        var raw = new AeroDBMessageStore(
            Substitute.For<SurrealDb.Net.ISurrealDbClient>(),
            Substitute.For<ILogger<AeroDBMessageStore>>(), null, null, ownsClient: false);
        var wrapper = new AeroDBCompositeMainMessageStore(raw, new AeroDBMainStoreIdentity("main", "main"));

        wrapper.ShouldBeAssignableTo<IMessageStoreWithAgentSupport>();
        wrapper.Role.ShouldBe(MessageStoreRole.Main);
        wrapper.Uri.Scheme.ShouldBe("wolverinedb");
        wrapper.Uri.ShouldNotBe(raw.Uri);

        await wrapper.DisposeAsync();

        raw.HasDisposed.ShouldBeFalse();
    }

    [Test]
    public void Composite_main_wrapper_reuses_one_agent_for_one_runtime_and_refuses_rebinding()
    {
        var raw = new AeroDBMessageStore(
            Substitute.For<SurrealDb.Net.ISurrealDbClient>(),
            Substitute.For<ILogger<AeroDBMessageStore>>(), null, null, ownsClient: false);
        var wrapper = new AeroDBCompositeMainMessageStore(raw, new AeroDBMainStoreIdentity("main", "main"));
        var firstRuntime = Substitute.For<IWolverineRuntime>();
        var secondRuntime = Substitute.For<IWolverineRuntime>();

        var first = wrapper.BuildAgent(firstRuntime);

        wrapper.BuildAgent(firstRuntime).ShouldBeSameAs(first);
        Should.Throw<InvalidOperationException>(() => wrapper.BuildAgent(secondRuntime));
    }
}
