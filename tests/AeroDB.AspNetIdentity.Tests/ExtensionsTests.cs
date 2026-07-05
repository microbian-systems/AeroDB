using AeroDB;
using AeroDB.AspNetIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
    public void AddAeroDBStores_WithKey_ShouldRegisterGenericStores()
    {
        var services = new ServiceCollection();
        var builder = new IdentityBuilder(typeof(IdentityUser<long>), typeof(IdentityRole<long>), services);

        builder.AddAeroDBStores<IdentityUser<long>, IdentityRole<long>, long>();

        services.ShouldContain(sd =>
            sd.ServiceType == typeof(IUserStore<IdentityUser<long>>) &&
            sd.ImplementationType == typeof(AeroDBUserStore<IdentityUser<long>, IdentityRole<long>, long>));

        services.ShouldContain(sd =>
            sd.ServiceType == typeof(IRoleStore<IdentityRole<long>>) &&
            sd.ImplementationType == typeof(AeroDBRoleStore<IdentityRole<long>, long>));
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

    [Test]
    public void IdentityConfigurator_ShouldRespectRequireUniqueEmail()
    {
        var services = new ServiceCollection();
        services.Configure<IdentityOptions>(o => o.User.RequireUniqueEmail = false);
        using var provider = services.BuildServiceProvider();
        var options = new StoreOptions { ServiceProvider = provider };
        var configurator = new AeroDBIdentityConfigurator<IdentityUser, IdentityRole, string>();

        configurator.Configure(provider, options);

        var mappings = GetMappings(options);
        var userMapping = mappings[typeof(IdentityUser)];
        var roleMapping = mappings[typeof(IdentityRole)];
        var userIndices = GetIndices(userMapping);
        var roleIndices = GetIndices(roleMapping);

        userIndices.Count(idx => idx.Columns.Contains(nameof(IdentityUser.NormalizedUserName)) && idx.IsUnique)
            .ShouldBe(1);
        userIndices.ShouldNotContain(idx => idx.Columns.Contains(nameof(IdentityUser.NormalizedEmail)));
        roleIndices.Count(idx => idx.Columns.Contains(nameof(IdentityRole.NormalizedName)) && idx.IsUnique)
            .ShouldBe(1);

        var fields = ((DocumentMapping<IdentityUser>)userMapping).FieldDefinitions;
        fields.ShouldContain(f => f.FieldName == "authenticator_key" && f.FieldType == "option<string>");
        fields.ShouldContain(f => f.FieldName == "recovery_codes" && f.FieldType == "option<array<string>>");
        fields.ShouldContain(f => f.FieldName == "role_ids" && f.FieldType == "option<array<string>>");
    }

    [Test]
    public void IdentityConfigurator_ShouldCreateUniqueEmailIndex_WhenRequired()
    {
        var services = new ServiceCollection();
        services.Configure<IdentityOptions>(o => o.User.RequireUniqueEmail = true);
        using var provider = services.BuildServiceProvider();
        var options = new StoreOptions { ServiceProvider = provider };
        var configurator = new AeroDBIdentityConfigurator<IdentityUser, IdentityRole, string>();

        configurator.Configure(provider, options);

        var userIndices = GetIndices(GetMappings(options)[typeof(IdentityUser)]);
        userIndices.Count(idx => idx.Columns.Contains(nameof(IdentityUser.NormalizedEmail)) && idx.IsUnique)
            .ShouldBe(1);
    }

    private static IReadOnlyList<IndexDefinition> GetIndices(DocumentMapping mapping)
    {
        var property = typeof(DocumentMapping)
            .GetProperty("Indices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (IReadOnlyList<IndexDefinition>)property.GetValue(mapping)!;
    }

    private static IReadOnlyDictionary<Type, DocumentMapping> GetMappings(StoreOptions options)
    {
        var property = typeof(SchemaOptions)
            .GetProperty("Mappings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        return (IReadOnlyDictionary<Type, DocumentMapping>)property.GetValue(options.Schema)!;
    }
}
