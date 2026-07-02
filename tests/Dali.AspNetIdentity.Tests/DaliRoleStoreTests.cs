using Dali;
using Dali.AspNetIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Dali.AspNetIdentity.Tests;

public class DaliRoleStoreTests
{
    private static IDocumentStore CreateStore(out IQuerySession querySession, out IDocumentSession documentSession, out ILogger<DaliRoleStore<IdentityRole>> logger)
    {
        var store = Substitute.For<IDocumentStore>();
        querySession = Substitute.For<IQuerySession>();
        documentSession = Substitute.For<IDocumentSession>();
        logger = Substitute.For<ILogger<DaliRoleStore<IdentityRole>>>();

        store.QuerySessionAsync(Arg.Any<CancellationToken>()).Returns(querySession);
        store.OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>()).Returns(documentSession);
        documentSession.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);

        return store;
    }

    // ── CreateAsync ───────────────────────────────────────────────────

    [Test]
    public async Task CreateAsync_ShouldReturnSuccess_WhenRoleCreated()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("admin");

        var result = await roleStore.CreateAsync(role, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        session.Received(1).Store(role);
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_ShouldReturnFailed_WhenExceptionThrown()
    {
        var store = CreateStore(out _, out var session, out var logger);
        session.When(s => s.Store(Arg.Any<IdentityRole>()))
            .Throw(new InvalidOperationException("DB error"));
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("admin");

        var result = await roleStore.CreateAsync(role, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    // ── UpdateAsync ───────────────────────────────────────────────────

    [Test]
    public async Task UpdateAsync_ShouldReturnSuccess_WhenRoleUpdated()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("admin");

        var result = await roleStore.UpdateAsync(role, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        session.Received(1).Store(role);
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_ShouldReturnFailed_WhenExceptionThrown()
    {
        var store = CreateStore(out _, out var session, out var logger);
        session.When(s => s.Store(Arg.Any<IdentityRole>()))
            .Throw(new InvalidOperationException("DB error"));
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("admin");

        var result = await roleStore.UpdateAsync(role, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    // ── DeleteAsync ───────────────────────────────────────────────────

    [Test]
    public async Task DeleteAsync_ShouldReturnSuccess_WhenRoleDeleted()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("admin");

        var result = await roleStore.DeleteAsync(role, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        session.Received(1).Delete(role);
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAsync_ShouldReturnFailed_WhenExceptionThrown()
    {
        var store = CreateStore(out _, out var session, out var logger);
        session.When(s => s.Delete(Arg.Any<IdentityRole>()))
            .Throw(new InvalidOperationException("DB error"));
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("admin");

        var result = await roleStore.DeleteAsync(role, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    // ── FindByIdAsync ─────────────────────────────────────────────────

    [Test]
    public async Task FindByIdAsync_ShouldReturnRole_WhenFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var role = new IdentityRole("admin") { Id = "role-1" };
        querySession.LoadAsync<IdentityRole>("role-1", Arg.Any<CancellationToken>()).Returns(role);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);

        var result = await roleStore.FindByIdAsync("role-1", CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe("role-1");
        result.Name.ShouldBe("admin");
    }

    [Test]
    public async Task FindByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        querySession.LoadAsync<IdentityRole>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdentityRole?)null);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);

        var result = await roleStore.FindByIdAsync("nonexistent", CancellationToken.None);

        result.ShouldBeNull();
    }

    // ── FindByNameAsync ───────────────────────────────────────────────

    [Test]
    public async Task FindByNameAsync_ShouldReturnRole_WhenFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var role = new IdentityRole("admin") { Id = "role-1", NormalizedName = "ADMIN" };
        var queryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        queryable.FirstOrDefaultAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(role);
        querySession.Query<IdentityRole>().Returns(queryable);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);

        var result = await roleStore.FindByNameAsync("ADMIN", CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe("role-1");
        result.NormalizedName.ShouldBe("ADMIN");
    }

    [Test]
    public async Task FindByNameAsync_ShouldReturnNull_WhenNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var queryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        queryable.FirstOrDefaultAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<IdentityRole, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((IdentityRole?)null);
        querySession.Query<IdentityRole>().Returns(queryable);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);

        var result = await roleStore.FindByNameAsync("NONEXISTENT", CancellationToken.None);

        result.ShouldBeNull();
    }

    // ── GetRoleIdAsync ────────────────────────────────────────────────

    [Test]
    public async Task GetRoleIdAsync_ShouldReturnRoleId()
    {
        var store = CreateStore(out _, out _, out var logger);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("admin") { Id = "role-42" };

        var result = await roleStore.GetRoleIdAsync(role, CancellationToken.None);

        result.ShouldBe("role-42");
    }

    // ── GetRoleNameAsync ──────────────────────────────────────────────

    [Test]
    public async Task GetRoleNameAsync_ShouldReturnRoleName()
    {
        var store = CreateStore(out _, out _, out var logger);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("editor");

        var result = await roleStore.GetRoleNameAsync(role, CancellationToken.None);

        result.ShouldBe("editor");
    }

    // ── SetRoleNameAsync ──────────────────────────────────────────────

    [Test]
    public async Task SetRoleNameAsync_ShouldSetRoleName()
    {
        var store = CreateStore(out _, out _, out var logger);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("old-name");

        await roleStore.SetRoleNameAsync(role, "new-name", CancellationToken.None);

        role.Name.ShouldBe("new-name");
    }

    // ── GetNormalizedRoleNameAsync ────────────────────────────────────

    [Test]
    public async Task GetNormalizedRoleNameAsync_ShouldReturnNormalizedName()
    {
        var store = CreateStore(out _, out _, out var logger);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("admin") { NormalizedName = "ADMIN" };

        var result = await roleStore.GetNormalizedRoleNameAsync(role, CancellationToken.None);

        result.ShouldBe("ADMIN");
    }

    // ── SetNormalizedRoleNameAsync ────────────────────────────────────

    [Test]
    public async Task SetNormalizedRoleNameAsync_ShouldSetNormalizedName()
    {
        var store = CreateStore(out _, out _, out var logger);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);
        var role = new IdentityRole("admin") { NormalizedName = "OLD" };

        await roleStore.SetNormalizedRoleNameAsync(role, "NEW", CancellationToken.None);

        role.NormalizedName.ShouldBe("NEW");
    }

    // ── Roles Property ────────────────────────────────────────────────

    [Test]
    public void Roles_ShouldCallQuerySessionAndReturnQuery()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var expectedRoles = new List<IdentityRole> { new("admin"), new("editor") };
        var queryable = Substitute.For<ISurrealDbQueryable<IdentityRole>>();
        querySession.Query<IdentityRole>().Returns(queryable);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);

        var roles = roleStore.Roles;

        roles.ShouldBe(queryable);
        querySession.Received(1).Query<IdentityRole>();
    }

    // ── Dispose ───────────────────────────────────────────────────────

    [Test]
    public void Dispose_ShouldNotThrow()
    {
        var store = CreateStore(out _, out _, out var logger);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);

        Should.NotThrow(() => roleStore.Dispose());
    }

    [Test]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var store = CreateStore(out _, out _, out var logger);
        var roleStore = new DaliRoleStore<IdentityRole>(store, logger);

        Should.NotThrow(() =>
        {
            roleStore.Dispose();
            roleStore.Dispose();
            roleStore.Dispose();
        });
    }
}
