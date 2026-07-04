using AeroDB;
using AeroDB.AspNetIdentity;

using Microsoft.AspNetCore.Identity;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering AeroDB-backed ASP.NET Core Identity stores
/// onto an <see cref="IdentityBuilder"/>.
/// </summary>
public static class AeroDBIdentityExtensions
{
    /// <summary>
    /// Registers <see cref="AeroDBUserStore{TUser, TRole}"/> and <see cref="AeroDBRoleStore{TRole}"/>
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

        return builder
            .AddRoleStore<AeroDBRoleStore<TRole>>()
            .AddUserStore<AeroDBUserStore<TUser, TRole>>();
    }
}
`