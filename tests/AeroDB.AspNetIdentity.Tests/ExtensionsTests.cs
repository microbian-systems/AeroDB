using AeroDB;
using AeroDB.AspNetIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace AeroDB.AspNetIdentity.Tests;

public class ExtensionsTests
{
    [Test]
    public void AddAeroDBStores_ShouldReturnIdentityBuilder()
    {
        var services = new ServiceCollection();
        var builder = new IdentityBuilder(typeof(IdentityUser), typeof(IdentityRole), services);

        var result = builder.AddAeroDBStores<IdentityUser, IdentityRole>();

        result.ShouldNotBeNull();
        result.ShouldBe(builder);
    }

    [Test]
    public void AddAeroDBStores_ShouldRegisterUserStore()
    {
        var services = new ServiceCollection();
        var builder = new IdentityBuilder(typeof(IdentityUser), typeof(IdentityRole), services);

        builder.AddAeroDBStores<IdentityUser, IdentityRole>();

        services.ShouldContain(sd =>
            sd.ServiceType == typeof(IUserStore<IdentityUser>) &&
            sd.ImplementationType == typeof(AeroDBUserStore<IdentityUser, IdentityRole>));
    }

    [Test]
    public void AddAeroDBStores_ShouldRegisterRoleStore()
    {
        var services = new ServiceCollection();
        var builder = new IdentityBuilder(typeof(IdentityUser), typeof(IdentityRole), services);

        builder.AddAeroDBStores<IdentityUser, IdentityRole>();

        services.ShouldContain(sd =>
            sd.ServiceType == typeof(IRoleStore<IdentityRole>) &&
            sd.ImplementationType == typeof(AeroDBRoleStore<IdentityRole>));
    }

    [Test]
    public void AddAeroDBStores_ThrowsOnNullBuilder()
    {
        IdentityBuilder builder = null!;

        Should.Throw<ArgumentNullException>(() =>
            builder.AddAeroDBStores<IdentityUser, IdentityRole>());
    }
}
