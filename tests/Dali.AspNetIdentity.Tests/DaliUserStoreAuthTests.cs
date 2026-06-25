using System.Linq.Expressions;
using Dali;
using Dali.AspNetIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Dali.AspNetIdentity.Tests;

public class DaliUserStoreAuthTests
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
        store.LightweightSessionAsync(Arg.Any<CancellationToken>()).Returns(documentSession);
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

    // ══════════════════════════════════════════════════════════════════
    //  IUserAuthenticatorKeyStore
    // ══════════════════════════════════════════════════════════════════

    // ── SetAuthenticatorKeyAsync ───────────────────────────────────────

    [Test]
    public async Task SetAuthenticatorKeyAsync_ShouldExecuteUpdate()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.SetAuthenticatorKeyAsync(user, "my-auth-key", CancellationToken.None);

        await session.Received(1).ExecuteSqlAsync(
            Arg.Is<string>(s => s.Contains("UPDATE") && s.Contains("authenticator_key")),
            Arg.Is<IReadOnlyDictionary<string, object?>>(d => d["key"]!.ToString() == "my-auth-key"),
            Arg.Any<CancellationToken>());
    }

    // ── GetAuthenticatorKeyAsync ───────────────────────────────────────

    [Test]
    public async Task GetAuthenticatorKeyAsync_ShouldReturnKey_WhenSet()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var authKeyResult = new AuthenticatorKeyResult { AuthenticatorKey = "stored-auth-key" };
        querySession.RawQueryAsync<AuthenticatorKeyResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<AuthenticatorKeyResult> { authKeyResult });
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetAuthenticatorKeyAsync(user, CancellationToken.None);

        result.ShouldBe("stored-auth-key");
    }

    [Test]
    public async Task GetAuthenticatorKeyAsync_ShouldReturnNull_WhenNotSet()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        querySession.RawQueryAsync<AuthenticatorKeyResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<AuthenticatorKeyResult>());
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetAuthenticatorKeyAsync(user, CancellationToken.None);

        result.ShouldBeNull();
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserTwoFactorRecoveryCodeStore
    // ══════════════════════════════════════════════════════════════════

    // ── ReplaceCodesAsync ─────────────────────────────────────────────

    [Test]
    public async Task ReplaceCodesAsync_ShouldExecuteUpdate()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var codes = new[] { "code1", "code2", "code3" };
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.ReplaceCodesAsync(user, codes, CancellationToken.None);

        await session.Received(1).ExecuteSqlAsync(
            Arg.Is<string>(s => s.Contains("UPDATE") && s.Contains("recovery_codes")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    // ── RedeemCodeAsync ───────────────────────────────────────────────

    [Test]
    public async Task RedeemCodeAsync_ShouldReturnTrue_WhenCodeIsValid()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var codes = new List<string> { "valid-code", "another-code" };

        // Mock GetRecoveryCodesAsync — RawQueryAsync<RecoveryCodesResult>
        var recoveryResult = new RecoveryCodesResult { RecoveryCodes = codes };
        session.RawQueryAsync<RecoveryCodesResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RecoveryCodesResult> { recoveryResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.RedeemCodeAsync(user, "valid-code", CancellationToken.None);

        result.ShouldBeTrue();

        // Should have removed the code and updated
        await session.Received(1).ExecuteSqlAsync(
            Arg.Is<string>(s => s.Contains("recovery_codes")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RedeemCodeAsync_ShouldReturnFalse_WhenCodeIsInvalid()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var codes = new List<string> { "valid-code", "another-code" };

        var recoveryResult = new RecoveryCodesResult { RecoveryCodes = codes };
        session.RawQueryAsync<RecoveryCodesResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RecoveryCodesResult> { recoveryResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.RedeemCodeAsync(user, "invalid-code", CancellationToken.None);

        result.ShouldBeFalse();

        // Should NOT have updated
        await session.DidNotReceive()
            .ExecuteSqlAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    // ── CountCodesAsync ───────────────────────────────────────────────

    [Test]
    public async Task CountCodesAsync_ShouldReturnCount_WhenCodesExist()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var codes = new List<string> { "c1", "c2", "c3" };

        var recoveryResult = new RecoveryCodesResult { RecoveryCodes = codes };
        querySession.RawQueryAsync<RecoveryCodesResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RecoveryCodesResult> { recoveryResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.CountCodesAsync(user, CancellationToken.None);

        result.ShouldBe(3);
    }

    [Test]
    public async Task CountCodesAsync_ShouldReturnZero_WhenNoCodes()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var recoveryResult = new RecoveryCodesResult { RecoveryCodes = [] };
        querySession.RawQueryAsync<RecoveryCodesResult>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RecoveryCodesResult> { recoveryResult });

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.CountCodesAsync(user, CancellationToken.None);

        result.ShouldBe(0);
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserAuthenticationTokenStore
    // ══════════════════════════════════════════════════════════════════

    // ── SetTokenAsync ─────────────────────────────────────────────────

    [Test]
    public async Task SetTokenAsync_ShouldStoreNewToken_WhenNotExists()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        // FirstOrDefaultAsync returns null (token doesn't exist)
        var tokenQueryable = Substitute.For<ISurrealDbQueryable<DaliUserToken>>();
        tokenQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<DaliUserToken, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((DaliUserToken?)null);
        session.Query<DaliUserToken>().Returns(tokenQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.SetTokenAsync(user, "Google", "access_token", "abc123", CancellationToken.None);

        // Verify new token was stored
        session.Received(1).Store(Arg.Is<DaliUserToken>(t =>
            t.UserId == "user-1" &&
            t.LoginProvider == "Google" &&
            t.Name == "access_token" &&
            t.Value == "abc123"));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetTokenAsync_ShouldUpdateExistingToken_WhenExists()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var existingToken = new DaliUserToken
        {
            Id = "t1",
            UserId = "user-1",
            LoginProvider = "Google",
            Name = "access_token",
            Value = "old-value"
        };

        var tokenQueryable = Substitute.For<ISurrealDbQueryable<DaliUserToken>>();
        tokenQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<DaliUserToken, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(existingToken);
        session.Query<DaliUserToken>().Returns(tokenQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.SetTokenAsync(user, "Google", "access_token", "new-value", CancellationToken.None);

        // Verify the existing token was updated and stored back
        existingToken.Value.ShouldBe("new-value");
        session.Received(1).Store(Arg.Is<DaliUserToken>(t => t.Id == "t1" && t.Value == "new-value"));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── RemoveTokenAsync ──────────────────────────────────────────────

    [Test]
    public async Task RemoveTokenAsync_ShouldDeleteToken_WhenFound()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var existingToken = new DaliUserToken
        {
            Id = "t1",
            UserId = "user-1",
            LoginProvider = "Google",
            Name = "access_token",
            Value = "abc"
        };

        var tokenQueryable = Substitute.For<ISurrealDbQueryable<DaliUserToken>>();
        tokenQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<DaliUserToken, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(existingToken);
        session.Query<DaliUserToken>().Returns(tokenQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemoveTokenAsync(user, "Google", "access_token", CancellationToken.None);

        session.Received(1).Delete(existingToken);
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemoveTokenAsync_ShouldNotDelete_WhenTokenNotFound()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var tokenQueryable = Substitute.For<ISurrealDbQueryable<DaliUserToken>>();
        tokenQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<DaliUserToken, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((DaliUserToken?)null);
        session.Query<DaliUserToken>().Returns(tokenQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemoveTokenAsync(user, "Google", "nonexistent", CancellationToken.None);

        session.DidNotReceive().Delete(Arg.Any<DaliUserToken>());
        await session.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── GetTokenAsync ─────────────────────────────────────────────────

    [Test]
    public async Task GetTokenAsync_ShouldReturnValue_WhenTokenFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var token = new DaliUserToken
        {
            Id = "t1",
            UserId = "user-1",
            LoginProvider = "Google",
            Name = "access_token",
            Value = "stored-value"
        };

        var tokenQueryable = Substitute.For<ISurrealDbQueryable<DaliUserToken>>();
        tokenQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<DaliUserToken, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(token);
        querySession.Query<DaliUserToken>().Returns(tokenQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetTokenAsync(user, "Google", "access_token", CancellationToken.None);

        result.ShouldBe("stored-value");
    }

    [Test]
    public async Task GetTokenAsync_ShouldReturnNull_WhenTokenNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var tokenQueryable = Substitute.For<ISurrealDbQueryable<DaliUserToken>>();
        tokenQueryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<DaliUserToken, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((DaliUserToken?)null);
        querySession.Query<DaliUserToken>().Returns(tokenQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetTokenAsync(user, "Google", "access_token", CancellationToken.None);

        result.ShouldBeNull();
    }
}
