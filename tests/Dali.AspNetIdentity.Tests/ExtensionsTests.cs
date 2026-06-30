using Dali;
using Dali.AspNetIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Dali.AspNetIdentity.Tests;

public class ExtensionsTests
{
    [Test]
    public void AddDaliStores_ShouldReturnIdentityBuilder()
    {
        var services = new ServiceCollection();
        var builder = new IdentityBuilder(typeof(IdentityUser), typeof(IdentityRole), services);

        var result = builder.AddDaliStores<IdentityUser, IdentityRole>();

        result.ShouldNotBeNull();
        result.ShouldBe(builder);
    }

    [Test]
    public void AddDaliStores_ShouldRegisterUserStore()
    {
        var services = new ServiceCollection();
        var builder = new IdentityBuilder(typeof(IdentityUser), typeof(IdentityRole), services);

        builder.AddDaliStores<IdentityUser, IdentityRole>();

        services.ShouldContain(sd =>
            sd.ServiceType == typeof(IUserStore<IdentityUser>) &&
            sd.ImplementationType == typeof(DaliUserStore<IdentityUser, IdentityRole>));
    }

    [Test]
    public void AddDaliStores_ShouldRegisterRoleStore()
    {
        var services = new ServiceCollection();
        var builder = new IdentityBuilder(typeof(IdentityUser), typeof(IdentityRole), services);

        builder.AddDaliStores<IdentityUser, IdentityRole>();

        services.ShouldContain(sd =>
            sd.ServiceType == typeof(IRoleStore<IdentityRole>) &&
            sd.ImplementationType == typeof(DaliRoleStore<IdentityRole>));
    }

    [Test]
    public void AddDaliStores_ThrowsOnNullBuilder()
    {
        IdentityBuilder builder = null!;

        Should.Throw<ArgumentNullException>(() =>
            builder.AddDaliStores<IdentityUser, IdentityRole>());
    }
}
