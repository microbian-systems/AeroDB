using System.Linq.Expressions;
using AeroDB.Sable;
using AeroDB.AspNetIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace AeroDB.AspNetIdentity.Tests;

public class AeroDBUserStoreLoginTests
{
    private static IDocumentStore CreateStore(
        out IQuerySession querySession,
        out IDocumentSession documentSession,
        out ILogger<AeroDBUserStore<IdentityUser, IdentityRole>> logger)
    {
        var store = Substitute.For<IDocumentStore>();
        querySession = Substitute.For<IQuerySession>();
        documentSession = Substitute.For<IDocumentSession>();
        logger = Substitute.For<ILogger<AeroDBUserStore<IdentityUser, IdentityRole>>>();

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

    // ── AddLoginAsync ─────────────────────────────────────────────────

    [Test]
    public async Task AddLoginAsync_ShouldStoreLogin()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var login = new UserLoginInfo("Google", "google-id-123", "Google");

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.AddLoginAsync(user, login, CancellationToken.None);

        session.Received(1).Store(Arg.Is<AeroDBUserLogin>(l =>
            l.UserId == "user-1" &&
            l.LoginProvider == "Google" &&
            l.ProviderKey == "google-id-123" &&
            l.ProviderDisplayName == "Google"));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── RemoveLoginAsync ──────────────────────────────────────────────

    [Test]
    public async Task RemoveLoginAsync_ShouldDeleteLogin_WhenFound()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var loginRecord = new AeroDBUserLogin
        {
            Id = "l1",
            UserId = "user-1",
            LoginProvider = "Google",
            ProviderKey = "google-id-123",
            ProviderDisplayName = "Google"
        };
        var loginQueryable = Substitute.For<ISurrealDbQueryable<AeroDBUserLogin>>();
        loginQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<AeroDBUserLogin, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(loginRecord);
        session.Query<AeroDBUserLogin>().Returns(loginQueryable);

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemoveLoginAsync(user, "Google", "google-id-123", CancellationToken.None);

        session.Received(1).Delete(loginRecord);
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemoveLoginAsync_ShouldNotDelete_WhenLoginNotFound()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var loginQueryable = Substitute.For<ISurrealDbQueryable<AeroDBUserLogin>>();
        loginQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<AeroDBUserLogin, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((AeroDBUserLogin?)null);
        session.Query<AeroDBUserLogin>().Returns(loginQueryable);

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemoveLoginAsync(user, "Google", "nonexistent", CancellationToken.None);

        session.DidNotReceive().Delete(Arg.Any<AeroDBUserLogin>());
        await session.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── GetLoginsAsync ────────────────────────────────────────────────

    [Test]
    public async Task GetLoginsAsync_ShouldReturnLogins_WhenLoginsExist()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var loginRecords = new List<AeroDBUserLogin>
        {
            new() { Id = "l1", UserId = "user-1", LoginProvider = "Google", ProviderKey = "g-1", ProviderDisplayName = "Google" },
            new() { Id = "l2", UserId = "user-1", LoginProvider = "GitHub", ProviderKey = "gh-1", ProviderDisplayName = "GitHub" }
        };
        var loginQueryable = CreateMockQueryable(loginRecords);
        querySession.Query<AeroDBUserLogin>().Returns(loginQueryable);

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetLoginsAsync(user, CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldContain(l => l.LoginProvider == "Google" && l.ProviderKey == "g-1");
        result.ShouldContain(l => l.LoginProvider == "GitHub" && l.ProviderKey == "gh-1");
    }

    [Test]
    public async Task GetLoginsAsync_ShouldReturnEmptyList_WhenNoLogins()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var loginQueryable = CreateMockQueryable(new List<AeroDBUserLogin>());
        querySession.Query<AeroDBUserLogin>().Returns(loginQueryable);

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetLoginsAsync(user, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    // ── FindByLoginAsync ──────────────────────────────────────────────

    [Test]
    public async Task FindByLoginAsync_ShouldReturnUser_WhenLoginFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var loginRecord = new AeroDBUserLogin
        {
            Id = "l1",
            UserId = "user-1",
            LoginProvider = "Google",
            ProviderKey = "google-id-123"
        };
        var loginQueryable = Substitute.For<ISurrealDbQueryable<AeroDBUserLogin>>();
        loginQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<AeroDBUserLogin, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(loginRecord);
        querySession.Query<AeroDBUserLogin>().Returns(loginQueryable);

        querySession.LoadAsync<IdentityUser>("user-1", Arg.Any<CancellationToken>()).Returns(user);

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindByLoginAsync("Google", "google-id-123", CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe("user-1");
    }

    [Test]
    public async Task FindByLoginAsync_ShouldReturnNull_WhenLoginNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);

        var loginQueryable = Substitute.For<ISurrealDbQueryable<AeroDBUserLogin>>();
        loginQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<AeroDBUserLogin, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((AeroDBUserLogin?)null);
        querySession.Query<AeroDBUserLogin>().Returns(loginQueryable);

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindByLoginAsync("Google", "nonexistent", CancellationToken.None);

        result.ShouldBeNull();
    }
}
