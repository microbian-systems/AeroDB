using AeroDB.WolverineFx;
using JasperFx.MultiTenancy;
using Shouldly;
using TUnit.Core;

namespace AeroDB.Tests;

public class AeroDBTenantRegistryStagingTests
{
    [Test]
    public void Staging_reserves_locations_without_exposing_active_routes()
    {
        var registry = new AeroDBTenantStoreRegistry();
        var alpha = Location("alpha");
        var stage = registry.Stage([alpha]);

        Should.Throw<UnknownTenantIdException>(() => registry.Find("alpha"));
        registry.FindStaged(stage, "alpha").ShouldBe(alpha);
        Should.Throw<InvalidOperationException>(() => registry.FindStaged(stage with { }, "alpha"));
        Should.Throw<UnknownTenantIdException>(() => registry.FindStaged(stage, "unknown"));
        registry.Activate(stage);

        registry.Find("alpha").ShouldBe(alpha);
        Should.Throw<InvalidOperationException>(() => registry.Activate(stage));
    }

    [Test]
    public void Staging_reservation_survives_abandonment_and_rejects_physical_reuse()
    {
        var registry = new AeroDBTenantStoreRegistry();
        var alpha = Location("alpha");
        registry.Stage([alpha]);

        Should.Throw<InvalidOperationException>(() => registry.Stage([new("alpha", "primary", "changed", "main")]));
        Should.Throw<InvalidOperationException>(() => registry.Stage([new("beta", "primary", alpha.Namespace, alpha.Database)]));
        Should.Throw<UnknownTenantIdException>(() => registry.Find("alpha"));
        registry.Activate(registry.Stage([alpha]));
        registry.Find("alpha").ShouldBe(alpha);
    }

    [Test]
    public void Rejected_candidate_does_not_partially_reserve_locations()
    {
        var registry = new AeroDBTenantStoreRegistry();
        var alpha = Location("alpha");
        Should.Throw<InvalidOperationException>(() => registry.Stage([alpha, alpha]));

        var replacement = new AeroDBTenantStoreLocation("alpha", "primary", "replacement", "main");
        registry.Activate(registry.Stage([replacement]));

        registry.Find("alpha").ShouldBe(replacement);
    }

    [Test]
    public void Foreign_stage_cannot_resolve_or_activate_in_another_registry()
    {
        var first = new AeroDBTenantStoreRegistry();
        var second = new AeroDBTenantStoreRegistry();
        var stage = first.Stage([Location("alpha")]);

        Should.Throw<InvalidOperationException>(() => second.FindStaged(stage, "alpha"));
        Should.Throw<InvalidOperationException>(() => second.Activate(stage));
        Should.Throw<UnknownTenantIdException>(() => second.Find("alpha"));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void Stale_stage_cannot_replace_a_newer_generation_or_resolve_resources(bool activateNewer)
    {
        var registry = new AeroDBTenantStoreRegistry();
        var alpha = Location("alpha");
        var stage = registry.Stage([alpha]);
        var beta = Location("beta");
        var newer = registry.Stage([beta]);
        if (activateNewer) registry.Activate(newer);

        Should.Throw<InvalidOperationException>(() => registry.FindStaged(stage, "alpha"));
        Should.Throw<InvalidOperationException>(() => registry.Activate(stage));
        if (activateNewer) registry.Find("beta").ShouldBe(beta);
        else Should.Throw<UnknownTenantIdException>(() => registry.Find("beta"));
        Should.Throw<UnknownTenantIdException>(() => registry.Find("alpha"));
    }

    private static AeroDBTenantStoreLocation Location(string tenant) =>
        new(tenant, "primary", "ca_" + tenant, "main");
}
