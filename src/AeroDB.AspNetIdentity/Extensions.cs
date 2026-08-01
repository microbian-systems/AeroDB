using AeroDB.AspNetIdentity;
using AeroDB.Sable;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

internal static class AeroDBIdentityTableNames
{
    public const string Users = "users";
    public const string Roles = "roles";
    public const string UserClaims = "user_claims";
    public const string UserLogins = "user_logins";
    public const string UserTokens = "user_tokens";
    public const string UserPasskeys = "user_passkeys";
}

/// <summary>
/// Extension methods for registering AeroDB.Sable-backed ASP.NET Core Identity stores
/// onto an <see cref="IdentityBuilder"/>.
/// </summary>
public static class AeroDBIdentityExtensions
{
    /// <summary>
    /// Registers <see cref="AeroDBUserStore{TUser,TRole}"/> and <see cref="AeroDBRoleStore{TRole}"/>
    /// with the identity system. Expects an <see cref="IDocumentStore"/> to already be
    /// registered in the service collection (as a singleton).
    /// </summary>
    /// <typeparam name="TUser">The user type, must inherit from <see cref="IdentityUser"/>.</typeparam>
    /// <typeparam name="TRole">The role type, must inherit from <see cref="IdentityRole"/>.</typeparam>
    /// <param name="builder">The <see cref="IdentityBuilder"/> from <c>AddIdentity</c> or <c>AddDefaultIdentity</c>.</param>
    /// <returns>The <see cref="IdentityBuilder"/> for chaining.</returns>
    public static IdentityBuilder AddAeroDBStores<TUser, TRole>(this IdentityBuilder builder)
        where TUser : IdentityUser
        where TRole : IdentityRole
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IConfigureAeroDB, AeroDBIdentityConfigurator<TUser, TRole, string>>();

        return builder
            .AddRoleStore<AeroDBRoleStore<TRole>>()
            .AddUserStore<AeroDBUserStore<TUser, TRole>>();
    }

    /// <summary>
    /// Registers <see cref="AeroDBUserStore{TUser, TRole, TKey}"/> and <see cref="AeroDBRoleStore{TRole, TKey}"/>
    /// with the identity system. Expects an <see cref="IDocumentStore"/> to already be
    /// registered in the service collection (as a singleton).
    /// </summary>
    /// <typeparam name="TUser">The user type, must inherit from <see cref="IdentityUser{TKey}"/>.</typeparam>
    /// <typeparam name="TRole">The role type, must inherit from <see cref="IdentityRole{TKey}"/>.</typeparam>
    /// <typeparam name="TKey">The identity key type.</typeparam>
    /// <param name="builder">The <see cref="IdentityBuilder"/> from <c>AddIdentity</c> or <c>AddDefaultIdentity</c>.</param>
    /// <returns>The <see cref="IdentityBuilder"/> for chaining.</returns>
    public static IdentityBuilder AddAeroDBStores<TUser, TRole, TKey>(this IdentityBuilder builder)
        where TUser : IdentityUser<TKey>
        where TRole : IdentityRole<TKey>
        where TKey : IEquatable<TKey>
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IConfigureAeroDB, AeroDBIdentityConfigurator<TUser, TRole, TKey>>();

        return builder
            .AddRoleStore<AeroDBRoleStore<TRole, TKey>>()
            .AddUserStore<AeroDBUserStore<TUser, TRole, TKey>>();
    }
}

internal sealed class AeroDBIdentityConfigurator<TUser, TRole, TKey> : IConfigureAeroDB
    where TUser : IdentityUser<TKey>
    where TRole : IdentityRole<TKey>
    where TKey : IEquatable<TKey>
{
    public void Configure(AeroDB.Sable.StoreOptions options)
        => Configure(options.ServiceProvider, options);

    public void Configure(IServiceProvider? services, AeroDB.Sable.StoreOptions options)
    {
        var identityOptions = services?.GetService<IOptions<IdentityOptions>>();
        var requireUniqueEmail = identityOptions?.Value.User.RequireUniqueEmail ?? true;

        var userMapping = options.Schema.For<TUser>()
            .TableName(AeroDBIdentityTableNames.Users)
            .Identity(x => x.Id)
            .UniqueIndex(x => x.NormalizedUserName)
            .Field("authenticator_key", f => f.FieldType = "option<string>")
            .Field("recovery_codes", f => f.FieldType = "option<array<string>>")
            .Field("role_ids", f => f.FieldType = "option<array<string>>");

        RemoveLegacyPascalCaseIdentityUserFields(userMapping);

        if (requireUniqueEmail)
            userMapping.UniqueIndex(x => x.NormalizedEmail);

        var roleMapping = options.Schema.For<TRole>()
            .TableName(AeroDBIdentityTableNames.Roles)
            .Identity(x => x.Id)
            .UniqueIndex(x => x.NormalizedName);

        RemoveLegacyPascalCaseIdentityRoleFields(roleMapping);

        options.Schema.For<AeroDB.AspNetIdentity.AeroDBUserClaim>()
            .TableName(AeroDBIdentityTableNames.UserClaims)
            .Identity(claim => claim.Id)
            .Index(claim => claim.UserId);

        options.Schema.For<AeroDB.AspNetIdentity.AeroDBUserLogin>()
            .TableName(AeroDBIdentityTableNames.UserLogins)
            .Identity(login => login.Id)
            .Index(login => login.UserId)
            .UniqueIndex(login => new { login.LoginProvider, login.ProviderKey });

        options.Schema.For<AeroDB.AspNetIdentity.AeroDBUserToken>()
            .TableName(AeroDBIdentityTableNames.UserTokens)
            .Identity(token => token.Id)
            .Index(token => token.UserId);

        options.Schema.For<AeroDB.AspNetIdentity.AeroDBUserPasskey>()
            .TableName(AeroDBIdentityTableNames.UserPasskeys)
            .Identity(passkey => passkey.Id)
            .Index(passkey => passkey.UserId);
    }

    private static void RemoveLegacyPascalCaseIdentityUserFields(DocumentMapping<TUser> mapping)
    {
        mapping
            .Field(nameof(IdentityUser<TKey>.UserName), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.NormalizedUserName), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.Email), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.NormalizedEmail), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.EmailConfirmed), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.PasswordHash), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.SecurityStamp), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.ConcurrencyStamp), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.PhoneNumber), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.PhoneNumberConfirmed), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.TwoFactorEnabled), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.LockoutEnd), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.LockoutEnabled), f => f.Remove = true)
            .Field(nameof(IdentityUser<TKey>.AccessFailedCount), f => f.Remove = true);
    }

    private static void RemoveLegacyPascalCaseIdentityRoleFields(DocumentMapping<TRole> mapping)
    {
        mapping
            .Field(nameof(IdentityRole<TKey>.Name), f => f.Remove = true)
            .Field(nameof(IdentityRole<TKey>.NormalizedName), f => f.Remove = true)
            .Field(nameof(IdentityRole<TKey>.ConcurrencyStamp), f => f.Remove = true);
    }
}
