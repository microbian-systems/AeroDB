using Dali;
using Dali.AspNetIdentity;

using Microsoft.AspNetCore.Identity;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering Dali-backed ASP.NET Core Identity stores
/// onto an <see cref="IdentityBuilder"/>.
/// </summary>
public static class DaliIdentityExtensions
{
    /// <summary>
    /// Registers <see cref="DaliUserStore{TUser, TRole}"/> and <see cref="DaliRoleStore{TRole}"/>
    /// with the identity system. Expects an <see cref="IDocumentStore"/> to already be
    /// registered in the service collection (as a singleton).
    /// </summary>
    /// <typeparam name="TUser">The user type, must inherit from <see cref="IdentityUser"/>.</typeparam>
    /// <typeparam name="TRole">The role type, must inherit from <see cref="IdentityRole"/>.</typeparam>
    /// <param name="builder">The <see cref="IdentityBuilder"/> from <c>AddIdentity</c> or <c>AddDefaultIdentity</c>.</param>
    /// <returns>The <see cref="IdentityBuilder"/> for chaining.</returns>
    public static IdentityBuilder AddDaliStores<TUser, TRole>(this IdentityBuilder builder)
        where TUser : IdentityUser
        where TRole : IdentityRole
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .AddRoleStore<DaliRoleStore<TRole>>()
            .AddUserStore<DaliUserStore<TUser, TRole>>();
    }
}
