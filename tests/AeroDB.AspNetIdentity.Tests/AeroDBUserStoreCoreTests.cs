using System.Linq.Expressions;
using AeroDB.Sable;
using AeroDB.AspNetIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace AeroDB.AspNetIdentity.Tests;

public class AeroDBUserStoreCoreTests
{
    // ── Helpers ───────────────────────────────────────────────────────

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

    /// <summary>
    /// Creates a mock queryable that survives LINQ Where() chaining.
    /// Returns the specified data from ToListAsync.
    /// </summary>
    private static ISableQueryable<T> CreateMockQueryable<T>(List<T> data) where T : class
    {
        var queryable = Substitute.For<ISableQueryable<T>>();
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
    //  Core CRUD
    // ══════════════════════════════════════════════════════════════════

    // ── CreateAsync ───────────────────────────────────────────────────

    [Test]
    public async Task CreateAsync_ShouldReturnSuccess_WhenUserCreated()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser");

        var result = await userStore.CreateAsync(user, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        session.Received(1).Store(user);
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_ShouldReturnFailed_WhenExceptionThrown()
    {
        var store = CreateStore(out _, out var session, out var logger);
        session.When(s => s.Store(Arg.Any<IdentityUser>()))
            .Throw(new InvalidOperationException("DB error"));
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser");

        var result = await userStore.CreateAsync(user, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    // ── UpdateAsync ───────────────────────────────────────────────────

    [Test]
    public async Task UpdateAsync_ShouldReturnSuccess_WhenUserUpdated()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser");

        var result = await userStore.UpdateAsync(user, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        session.Received(1).Update(user);
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_ShouldReturnFailed_WhenExceptionThrown()
    {
        var store = CreateStore(out _, out var session, out var logger);
        session.When(s => s.Update(Arg.Any<IdentityUser>()))
            .Throw(new InvalidOperationException("DB error"));
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser");

        var result = await userStore.UpdateAsync(user, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    // ── DeleteAsync ───────────────────────────────────────────────────

    [Test]
    public async Task DeleteAsync_ShouldReturnSuccess_AndDeleteAssociatedRecords()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        // Mock claim records
        var claims = new List<AeroDBUserClaim>
        {
            new() { Id = "c1", UserId = "user-1", ClaimType = "role", ClaimValue = "admin" }
        };
        var claimQueryable = CreateMockQueryable(claims);
        session.Query<AeroDBUserClaim>().Returns(claimQueryable);

        // Mock login records
        var logins = new List<AeroDBUserLogin>
        {
            new() { Id = "l1", UserId = "user-1", LoginProvider = "Google", ProviderKey = "123" }
        };
        var loginQueryable = CreateMockQueryable(logins);
        session.Query<AeroDBUserLogin>().Returns(loginQueryable);

        // Mock token records
        var tokens = new List<AeroDBUserToken>
        {
            new() { Id = "t1", UserId = "user-1", LoginProvider = "Google", Name = "access_token", Value = "abc" }
        };
        var tokenQueryable = CreateMockQueryable(tokens);
        session.Query<AeroDBUserToken>().Returns(tokenQueryable);

        // Mock passkey records
        var passkeys = new List<AeroDBUserPasskey>
        {
            new() { Id = "p1", UserId = "user-1", CredentialId = [1, 2, 3] }
        };
        var passkeyQueryable = CreateMockQueryable(passkeys);
        session.Query<AeroDBUserPasskey>().Returns(passkeyQueryable);

        var result = await userStore.DeleteAsync(user, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        session.Received(1).Delete(claims[0]);
        session.Received(1).Delete(logins[0]);
        session.Received(1).Delete(tokens[0]);
        session.Received(1).Delete(passkeys[0]);
        session.Received(1).Delete(user);
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAsync_ShouldReturnFailed_WhenExceptionThrown()
    {
        var store = CreateStore(out _, out var session, out var logger);
        // Make Delete<T> throw
        session.When(s => s.Delete(Arg.Any<IdentityUser>()))
            .Throw(new InvalidOperationException("DB error"));
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser");

        var result = await userStore.DeleteAsync(user, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
    }

    // ── FindByIdAsync ─────────────────────────────────────────────────

    [Test]
    public async Task FindByIdAsync_ShouldReturnUser_WhenFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        querySession.LoadAsync<IdentityUser>("user-1", Arg.Any<CancellationToken>()).Returns(user);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindByIdAsync("user-1", CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe("user-1");
        result.UserName.ShouldBe("testuser");
    }

    [Test]
    public async Task FindByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        querySession.LoadAsync<IdentityUser>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IdentityUser?)null);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindByIdAsync("nonexistent", CancellationToken.None);

        result.ShouldBeNull();
    }

    // ── FindByNameAsync ───────────────────────────────────────────────

    [Test]
    public async Task FindByNameAsync_ShouldReturnUser_WhenFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1", NormalizedUserName = "TESTUSER" };
        var queryable = Substitute.For<ISableQueryable<IdentityUser>>();
        queryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityUser, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        querySession.Query<IdentityUser>().Returns(queryable);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindByNameAsync("TESTUSER", CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe("user-1");
        result.NormalizedUserName.ShouldBe("TESTUSER");
    }

    [Test]
    public async Task FindByNameAsync_ShouldReturnNull_WhenNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var queryable = Substitute.For<ISableQueryable<IdentityUser>>();
        queryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityUser, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((IdentityUser?)null);
        querySession.Query<IdentityUser>().Returns(queryable);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindByNameAsync("NONEXISTENT", CancellationToken.None);

        result.ShouldBeNull();
    }

    // ── GetUserIdAsync ────────────────────────────────────────────────

    [Test]
    public async Task GetUserIdAsync_ShouldReturnUserId()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { Id = "user-42" };

        var result = await userStore.GetUserIdAsync(user, CancellationToken.None);

        result.ShouldBe("user-42");
    }

    // ── GetUserNameAsync / SetUserNameAsync ───────────────────────────

    [Test]
    public async Task GetUserNameAsync_ShouldReturnUserName()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("myuser");

        var result = await userStore.GetUserNameAsync(user, CancellationToken.None);

        result.ShouldBe("myuser");
    }

    [Test]
    public async Task SetUserNameAsync_ShouldSetUserName()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("oldname");

        await userStore.SetUserNameAsync(user, "newname", CancellationToken.None);

        user.UserName.ShouldBe("newname");
    }

    // ── GetNormalizedUserNameAsync / SetNormalizedUserNameAsync ───────

    [Test]
    public async Task GetNormalizedUserNameAsync_ShouldReturnNormalizedUserName()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { NormalizedUserName = "TESTUSER" };

        var result = await userStore.GetNormalizedUserNameAsync(user, CancellationToken.None);

        result.ShouldBe("TESTUSER");
    }

    [Test]
    public async Task SetNormalizedUserNameAsync_ShouldSetNormalizedUserName()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { NormalizedUserName = "OLD" };

        await userStore.SetNormalizedUserNameAsync(user, "NEW", CancellationToken.None);

        user.NormalizedUserName.ShouldBe("NEW");
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserPasswordStore
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetPasswordHashAsync_ShouldReturnPasswordHash()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { PasswordHash = "AQAAAA..." };

        var result = await userStore.GetPasswordHashAsync(user, CancellationToken.None);

        result.ShouldBe("AQAAAA...");
    }

    [Test]
    public async Task SetPasswordHashAsync_ShouldSetPasswordHash()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser");

        await userStore.SetPasswordHashAsync(user, "NEWHASH", CancellationToken.None);

        user.PasswordHash.ShouldBe("NEWHASH");
    }

    [Test]
    public async Task HasPasswordAsync_ShouldReturnTrue_WhenHashIsSet()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { PasswordHash = "hash" };

        var result = await userStore.HasPasswordAsync(user, CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task HasPasswordAsync_ShouldReturnFalse_WhenHashIsNull()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { PasswordHash = null };

        var result = await userStore.HasPasswordAsync(user, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task HasPasswordAsync_ShouldReturnFalse_WhenHashIsEmpty()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { PasswordHash = "" };

        var result = await userStore.HasPasswordAsync(user, CancellationToken.None);

        result.ShouldBeFalse();
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserEmailStore
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetEmailAsync_ShouldReturnEmail()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { Email = "test@example.com" };

        var result = await userStore.GetEmailAsync(user, CancellationToken.None);

        result.ShouldBe("test@example.com");
    }

    [Test]
    public async Task SetEmailAsync_ShouldSetEmail()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { Email = "old@example.com" };

        await userStore.SetEmailAsync(user, "new@example.com", CancellationToken.None);

        user.Email.ShouldBe("new@example.com");
    }

    [Test]
    public async Task GetEmailConfirmedAsync_ShouldReturnTrue_WhenConfirmed()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { EmailConfirmed = true };

        var result = await userStore.GetEmailConfirmedAsync(user, CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task GetEmailConfirmedAsync_ShouldReturnFalse_WhenNotConfirmed()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { EmailConfirmed = false };

        var result = await userStore.GetEmailConfirmedAsync(user, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task SetEmailConfirmedAsync_ShouldSetEmailConfirmed()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { EmailConfirmed = false };

        await userStore.SetEmailConfirmedAsync(user, true, CancellationToken.None);

        user.EmailConfirmed.ShouldBeTrue();
    }

    [Test]
    public async Task GetNormalizedEmailAsync_ShouldReturnNormalizedEmail()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { NormalizedEmail = "TEST@EXAMPLE.COM" };

        var result = await userStore.GetNormalizedEmailAsync(user, CancellationToken.None);

        result.ShouldBe("TEST@EXAMPLE.COM");
    }

    [Test]
    public async Task SetNormalizedEmailAsync_ShouldSetNormalizedEmail()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { NormalizedEmail = "OLD@EXAMPLE.COM" };

        await userStore.SetNormalizedEmailAsync(user, "NEW@EXAMPLE.COM", CancellationToken.None);

        user.NormalizedEmail.ShouldBe("NEW@EXAMPLE.COM");
    }

    [Test]
    public async Task FindByEmailAsync_ShouldReturnUser_WhenFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1", NormalizedEmail = "TEST@EXAMPLE.COM" };
        var queryable = Substitute.For<ISableQueryable<IdentityUser>>();
        queryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityUser, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        querySession.Query<IdentityUser>().Returns(queryable);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindByEmailAsync("TEST@EXAMPLE.COM", CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe("user-1");
        result.NormalizedEmail.ShouldBe("TEST@EXAMPLE.COM");
    }

    [Test]
    public async Task FindByEmailAsync_ShouldReturnNull_WhenNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var queryable = Substitute.For<ISableQueryable<IdentityUser>>();
        queryable.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<IdentityUser, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((IdentityUser?)null);
        querySession.Query<IdentityUser>().Returns(queryable);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindByEmailAsync("NOSUCH@EXAMPLE.COM", CancellationToken.None);

        result.ShouldBeNull();
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserPhoneNumberStore
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetPhoneNumberAsync_ShouldReturnPhoneNumber()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { PhoneNumber = "+1234567890" };

        var result = await userStore.GetPhoneNumberAsync(user, CancellationToken.None);

        result.ShouldBe("+1234567890");
    }

    [Test]
    public async Task SetPhoneNumberAsync_ShouldSetPhoneNumber()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { PhoneNumber = "+1111111111" };

        await userStore.SetPhoneNumberAsync(user, "+2222222222", CancellationToken.None);

        user.PhoneNumber.ShouldBe("+2222222222");
    }

    [Test]
    public async Task GetPhoneNumberConfirmedAsync_ShouldReturnTrue_WhenConfirmed()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { PhoneNumberConfirmed = true };

        var result = await userStore.GetPhoneNumberConfirmedAsync(user, CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task GetPhoneNumberConfirmedAsync_ShouldReturnFalse_WhenNotConfirmed()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { PhoneNumberConfirmed = false };

        var result = await userStore.GetPhoneNumberConfirmedAsync(user, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task SetPhoneNumberConfirmedAsync_ShouldSetPhoneNumberConfirmed()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { PhoneNumberConfirmed = false };

        await userStore.SetPhoneNumberConfirmedAsync(user, true, CancellationToken.None);

        user.PhoneNumberConfirmed.ShouldBeTrue();
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserTwoFactorStore
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetTwoFactorEnabledAsync_ShouldReturnTrue_WhenEnabled()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { TwoFactorEnabled = true };

        var result = await userStore.GetTwoFactorEnabledAsync(user, CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task GetTwoFactorEnabledAsync_ShouldReturnFalse_WhenDisabled()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { TwoFactorEnabled = false };

        var result = await userStore.GetTwoFactorEnabledAsync(user, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task SetTwoFactorEnabledAsync_ShouldSetTwoFactorEnabled()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { TwoFactorEnabled = false };

        await userStore.SetTwoFactorEnabledAsync(user, true, CancellationToken.None);

        user.TwoFactorEnabled.ShouldBeTrue();
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserSecurityStampStore
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetSecurityStampAsync_ShouldReturnSecurityStamp()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { SecurityStamp = "stamp-123" };

        var result = await userStore.GetSecurityStampAsync(user, CancellationToken.None);

        result.ShouldBe("stamp-123");
    }

    [Test]
    public async Task SetSecurityStampAsync_ShouldSetSecurityStamp()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { SecurityStamp = "old-stamp" };

        await userStore.SetSecurityStampAsync(user, "new-stamp", CancellationToken.None);

        user.SecurityStamp.ShouldBe("new-stamp");
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserLockoutStore
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetLockoutEndDateAsync_ShouldReturnLockoutEnd()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var lockoutEnd = DateTimeOffset.UtcNow.AddDays(1);
        var user = new IdentityUser("testuser") { LockoutEnd = lockoutEnd };

        var result = await userStore.GetLockoutEndDateAsync(user, CancellationToken.None);

        result.ShouldBe(lockoutEnd);
    }

    [Test]
    public async Task GetLockoutEndDateAsync_ShouldReturnNull_WhenNotLocked()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { LockoutEnd = null };

        var result = await userStore.GetLockoutEndDateAsync(user, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task SetLockoutEndDateAsync_ShouldSetLockoutEnd()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { LockoutEnd = null };
        var lockoutEnd = DateTimeOffset.UtcNow.AddDays(1);

        await userStore.SetLockoutEndDateAsync(user, lockoutEnd, CancellationToken.None);

        user.LockoutEnd.ShouldBe(lockoutEnd);
    }

    [Test]
    public async Task SetLockoutEndDateAsync_ShouldClearLockout_WhenNull()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { LockoutEnd = DateTimeOffset.UtcNow.AddDays(1) };

        await userStore.SetLockoutEndDateAsync(user, null, CancellationToken.None);

        user.LockoutEnd.ShouldBeNull();
    }

    [Test]
    public async Task IncrementAccessFailedCountAsync_ShouldIncrementFromZero()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { AccessFailedCount = 0 };

        var result = await userStore.IncrementAccessFailedCountAsync(user, CancellationToken.None);

        result.ShouldBe(1);
        user.AccessFailedCount.ShouldBe(1);
    }

    [Test]
    public async Task IncrementAccessFailedCountAsync_ShouldIncrementFromFive()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { AccessFailedCount = 5 };

        var result = await userStore.IncrementAccessFailedCountAsync(user, CancellationToken.None);

        result.ShouldBe(6);
        user.AccessFailedCount.ShouldBe(6);
    }

    [Test]
    public async Task ResetAccessFailedCountAsync_ShouldResetToZero()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { AccessFailedCount = 5 };

        await userStore.ResetAccessFailedCountAsync(user, CancellationToken.None);

        user.AccessFailedCount.ShouldBe(0);
    }

    [Test]
    public async Task GetAccessFailedCountAsync_ShouldReturnCount()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { AccessFailedCount = 3 };

        var result = await userStore.GetAccessFailedCountAsync(user, CancellationToken.None);

        result.ShouldBe(3);
    }

    [Test]
    public async Task GetLockoutEnabledAsync_ShouldReturnTrue_WhenEnabled()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { LockoutEnabled = true };

        var result = await userStore.GetLockoutEnabledAsync(user, CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task GetLockoutEnabledAsync_ShouldReturnFalse_WhenDisabled()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { LockoutEnabled = false };

        var result = await userStore.GetLockoutEnabledAsync(user, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task SetLockoutEnabledAsync_ShouldSetLockoutEnabled()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);
        var user = new IdentityUser("testuser") { LockoutEnabled = false };

        await userStore.SetLockoutEnabledAsync(user, true, CancellationToken.None);

        user.LockoutEnabled.ShouldBeTrue();
    }

    // ══════════════════════════════════════════════════════════════════
    //  IQueryableUserStore — Users Property
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Users_ShouldCallQuerySessionAndReturnQuery()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var expectedUsers = new List<IdentityUser> { new("alice"), new("bob") };
        var queryable = Substitute.For<ISableQueryable<IdentityUser>>();
        querySession.Query<IdentityUser>().Returns(queryable);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var users = userStore.Users;

        users.ShouldBe(queryable);
        querySession.Received(1).Query<IdentityUser>();
    }

    // ══════════════════════════════════════════════════════════════════
    //  IDisposable
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Dispose_ShouldNotThrow()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        Should.NotThrow(() => userStore.Dispose());
    }

    [Test]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var store = CreateStore(out _, out _, out var logger);
        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        Should.NotThrow(() =>
        {
            userStore.Dispose();
            userStore.Dispose();
            userStore.Dispose();
        });
    }
}
