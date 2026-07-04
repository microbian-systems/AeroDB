using System.Linq.Expressions;
using AeroDB;
using AeroDB.AspNetIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace AeroDB.AspNetIdentity.Tests;

public class DaliUserStoreRoleTests
{
    private static IDocumentStore CreateStore(
        out IQuerySession querySession,
        out IDocumentSession documentSession,
        out ILogger<DaliUserStore<IdentityUser, IdentityRole>> logger)
    {
        var store = Substitute.For<IDocumentStore>();
        querySession = Substitute.For<IQuerySession>();
        documentSession = Substitute.For<IDocumentSession>();
        logger = Substitute.For<ILogger<DaliUserStore<IdentityUser, IdentityRole>>>();

        store.QuerySessionAsync(Arg.Any<CancellationToken>()).Returns(querySession);
        store.OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>()).Returns(documentSession);
        documentSession.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);

        return store;
    }

    private static ISurrealDbQueryable<T> CreateMockQueryable<T>(List<T> data) where T : class
    {
        var queryable = Substitute.For<ISurrealDbQueryable<T>>();
                queryable.ToListAsync(Arg.Any<CancellationToken>()).Returns(data);

        var provider = Substitute.For<IQueryProvider>();
        provider.CreateQuery<T>(Arg.Any<Expression>()).Returns(queryable);
        provider.CreateQuery(Arg.Any<Expression>()).Returns(queryable);

        queryable.Provider.Returns(provider);
        queryable.ElementType.Returns(typeof(T));
        queryable.Expression.Returns(Expression.Constant(queryable));

        return queryable;
    }

    // ── AddToRoleAsync ────────────────────────────────────────────────

    [Test]
    public async Task AddToRoleAsync_ShouldAddRole_WhenRoleExistsAndNotAlreadyInRole()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var role = new IdentityRole("admin") { Id = "role-1", NormalizedName = "ADMIN" };

        // Mock role lookup
        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(role);
        session.Query<IdentityRole>().Returns(roleQueryable);

        // Mock GetRoleIdsAsync — returns empty list initially
        var roleIdsResult = new RoleIdsResult { RoleIds = [] };
        session.RawQueryAsync<RoleIdsResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RoleIdsResult> { roleIdsResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.AddToRoleAsync(user, "ADMIN", CancellationToken.None);

        // Should have set role_ids via ExecuteSqlAsync
        await session.Received(1).ExecuteSqlAsync(
            Arg.Is<string>(s => s.Contains("SET role_ids")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());

        roleIdsResult.RoleIds.ShouldContain("role-1");
    }

    [Test]
    public async Task AddToRoleAsync_ShouldNotAdd_WhenRoleNotFound()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((IdentityRole?)null);
        session.Query<IdentityRole>().Returns(roleQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.AddToRoleAsync(user, "ADMIN", CancellationToken.None);

        // Should not set anything
        await session.DidNotReceive()
            .ExecuteSqlAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AddToRoleAsync_ShouldNotAddDuplicate_WhenAlreadyInRole()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var role = new IdentityRole("admin") { Id = "role-1", NormalizedName = "ADMIN" };

        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(role);
        session.Query<IdentityRole>().Returns(roleQueryable);

        // Mock GetRoleIdsAsync — user already has role-1
        var roleIdsResult = new RoleIdsResult { RoleIds = ["role-1"] };
        session.RawQueryAsync<RoleIdsResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RoleIdsResult> { roleIdsResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.AddToRoleAsync(user, "ADMIN", CancellationToken.None);

        // Should not set role_ids again
        await session.DidNotReceive()
            .ExecuteSqlAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    // ── RemoveFromRoleAsync ───────────────────────────────────────────

    [Test]
    public async Task RemoveFromRoleAsync_ShouldRemoveRole_WhenUserInRole()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var role = new IdentityRole("admin") { Id = "role-1", NormalizedName = "ADMIN" };

        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(role);
        session.Query<IdentityRole>().Returns(roleQueryable);

        // Mock GetRoleIdsAsync — user has role-1
        var roleIdsResult = new RoleIdsResult { RoleIds = ["role-1"] };
        session.RawQueryAsync<RoleIdsResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RoleIdsResult> { roleIdsResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemoveFromRoleAsync(user, "ADMIN", CancellationToken.None);

        // Should update role_ids via ExecuteSqlAsync
        await session.Received(1).ExecuteSqlAsync(
            Arg.Is<string>(s => s.Contains("SET role_ids") || s.Contains("SET role_ids = NONE")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemoveFromRoleAsync_ShouldNotRemove_WhenUserNotInRole()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var role = new IdentityRole("admin") { Id = "role-1", NormalizedName = "ADMIN" };

        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(role);
        session.Query<IdentityRole>().Returns(roleQueryable);

        // Mock GetRoleIdsAsync — empty
        var roleIdsResult = new RoleIdsResult { RoleIds = [] };
        session.RawQueryAsync<RoleIdsResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RoleIdsResult> { roleIdsResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemoveFromRoleAsync(user, "ADMIN", CancellationToken.None);

        await session.DidNotReceive()
            .ExecuteSqlAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemoveFromRoleAsync_ShouldBeNoOp_WhenRoleNotFound()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((IdentityRole?)null);
        session.Query<IdentityRole>().Returns(roleQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemoveFromRoleAsync(user, "ADMIN", CancellationToken.None);

        await session.DidNotReceive()
            .ExecuteSqlAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    // ── GetRolesAsync ─────────────────────────────────────────────────

    [Test]
    public async Task GetRolesAsync_ShouldReturnRoleNames_WhenUserHasRoles()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        // Mock GetRoleIdsAsync
        var roleIdsResult = new RoleIdsResult { RoleIds = ["role-1", "role-2"] };
        querySession.RawQueryAsync<RoleIdsResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RoleIdsResult> { roleIdsResult });

        // Mock role query — the store does Query<TRole>().Where(r => roleIds.Contains(r.Id!)).ToListAsync()
        var roles = new List<IdentityRole>
        {
            new("admin") { Id = "role-1", Name = "Admin" },
            new("editor") { Id = "role-2", Name = "Editor" }
        };
        var roleQueryable = CreateMockQueryable(roles);
        querySession.Query<IdentityRole>().Returns(roleQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetRolesAsync(user, CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldContain("Admin");
        result.ShouldContain("Editor");
    }

    [Test]
    public async Task GetRolesAsync_ShouldReturnEmpty_WhenUserHasNoRoles()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var roleIdsResult = new RoleIdsResult { RoleIds = [] };
        querySession.RawQueryAsync<RoleIdsResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RoleIdsResult> { roleIdsResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetRolesAsync(user, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    // ── IsInRoleAsync ─────────────────────────────────────────────────

    [Test]
    public async Task IsInRoleAsync_ShouldReturnTrue_WhenUserInRole()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var role = new IdentityRole("admin") { Id = "role-1", NormalizedName = "ADMIN" };

        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(role);
        querySession.Query<IdentityRole>().Returns(roleQueryable);

        var roleIdsResult = new RoleIdsResult { RoleIds = ["role-1"] };
        querySession.RawQueryAsync<RoleIdsResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RoleIdsResult> { roleIdsResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.IsInRoleAsync(user, "ADMIN", CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task IsInRoleAsync_ShouldReturnFalse_WhenUserNotInRole()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var role = new IdentityRole("admin") { Id = "role-1", NormalizedName = "ADMIN" };

        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(role);
        querySession.Query<IdentityRole>().Returns(roleQueryable);

        var roleIdsResult = new RoleIdsResult { RoleIds = ["role-2"] };
        querySession.RawQueryAsync<RoleIdsResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RoleIdsResult> { roleIdsResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.IsInRoleAsync(user, "ADMIN", CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task IsInRoleAsync_ShouldReturnFalse_WhenRoleNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((IdentityRole?)null);
        querySession.Query<IdentityRole>().Returns(roleQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.IsInRoleAsync(user, "ADMIN", CancellationToken.None);

        result.ShouldBeFalse();
    }

    // ── GetUsersInRoleAsync ───────────────────────────────────────────

    [Test]
    public async Task GetUsersInRoleAsync_ShouldReturnUsers_WhenUsersInRole()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var role = new IdentityRole("admin") { Id = "role-1", NormalizedName = "ADMIN" };

        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(role);
        querySession.Query<IdentityRole>().Returns(roleQueryable);

        var users = new List<IdentityUser>
        {
            new("alice") { Id = "user-1" },
            new("bob") { Id = "user-2" }
        };
        querySession.RawQueryAsync<IdentityUser>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(users);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetUsersInRoleAsync("ADMIN", CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldContain(u => u.Id == "user-1");
        result.ShouldContain(u => u.Id == "user-2");
    }

    [Test]
    public async Task GetUsersInRoleAsync_ShouldReturnEmpty_WhenNoUsersInRole()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var role = new IdentityRole("admin") { Id = "role-1", NormalizedName = "ADMIN" };

        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(role);
        querySession.Query<IdentityRole>().Returns(roleQueryable);

        querySession.RawQueryAsync<IdentityUser>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<IdentityUser>());

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetUsersInRoleAsync("ADMIN", CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetUsersInRoleAsync_ShouldReturnEmpty_WhenRoleNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var roleQueryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        roleQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((IdentityRole?)null);
        querySession.Query<IdentityRole>().Returns(roleQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetUsersInRoleAsync("ADMIN", CancellationToken.None);

        result.ShouldBeEmpty();
    }
}
