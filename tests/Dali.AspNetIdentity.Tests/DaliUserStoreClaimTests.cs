using System.Linq.Expressions;
using System.Security.Claims;
using Dali;
using Dali.AspNetIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Dali.AspNetIdentity.Tests;

public class DaliUserStoreClaimTests
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

    // ── GetClaimsAsync ───────────────────────────────────────────────

    [Test]
    public async Task GetClaimsAsync_ShouldReturnClaims_WhenClaimsExist()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var claimRecords = new List<DaliUserClaim>
        {
            new() { Id = "c1", UserId = "user-1", ClaimType = "role", ClaimValue = "admin" },
            new() { Id = "c2", UserId = "user-1", ClaimType = "permission", ClaimValue = "read" }
        };
        var claimQueryable = CreateMockQueryable(claimRecords);
        querySession.Query<DaliUserClaim>().Returns(claimQueryable);
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetClaimsAsync(user, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.ShouldContain(c => c.Type == "role" && c.Value == "admin");
        result.ShouldContain(c => c.Type == "permission" && c.Value == "read");
    }

    [Test]
    public async Task GetClaimsAsync_ShouldReturnEmptyList_WhenNoClaims()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var claimQueryable = CreateMockQueryable(new List<DaliUserClaim>());
        querySession.Query<DaliUserClaim>().Returns(claimQueryable);
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetClaimsAsync(user, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    // ── AddClaimsAsync ───────────────────────────────────────────────

    [Test]
    public async Task AddClaimsAsync_ShouldStoreEachClaim()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var claims = new List<Claim>
        {
            new("role", "admin"),
            new("permission", "read")
        };
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.AddClaimsAsync(user, claims, CancellationToken.None);

        session.Received(1).Store(Arg.Is<DaliUserClaim>(c =>
            c.UserId == "user-1" && c.ClaimType == "role" && c.ClaimValue == "admin"));
        session.Received(1).Store(Arg.Is<DaliUserClaim>(c =>
            c.UserId == "user-1" && c.ClaimType == "permission" && c.ClaimValue == "read"));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── ReplaceClaimAsync ─────────────────────────────────────────────

    [Test]
    public async Task ReplaceClaimAsync_ShouldReplaceExistingClaim()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var claim = new Claim("role", "admin");
        var newClaim = new Claim("role", "superadmin");

        var existingRecords = new List<DaliUserClaim>
        {
            new() { Id = "c1", UserId = "user-1", ClaimType = "role", ClaimValue = "admin" }
        };
        var claimQueryable = CreateMockQueryable(existingRecords);
        session.Query<DaliUserClaim>().Returns(claimQueryable);
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.ReplaceClaimAsync(user, claim, newClaim, CancellationToken.None);

        // Verify the record was updated and stored
        session.Received(1).Store(Arg.Is<DaliUserClaim>(c =>
            c.Id == "c1" && c.ClaimType == "role" && c.ClaimValue == "superadmin"));
        existingRecords[0].ClaimType.ShouldBe("role");
        existingRecords[0].ClaimValue.ShouldBe("superadmin");
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReplaceClaimAsync_ShouldBeNoOp_WhenClaimNotFound()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var claim = new Claim("role", "nonexistent");
        var newClaim = new Claim("role", "updated");

        var claimQueryable = CreateMockQueryable(new List<DaliUserClaim>());
        session.Query<DaliUserClaim>().Returns(claimQueryable);
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.ReplaceClaimAsync(user, claim, newClaim, CancellationToken.None);

        session.DidNotReceive().Store(Arg.Any<DaliUserClaim>());
        // SaveChangesAsync is always called by the store even when no records match
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── RemoveClaimsAsync ─────────────────────────────────────────────

    [Test]
    public async Task RemoveClaimsAsync_ShouldDeleteMatchingClaims()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var claimToRemove = new Claim("role", "admin");

        var existingRecords = new List<DaliUserClaim>
        {
            new() { Id = "c1", UserId = "user-1", ClaimType = "role", ClaimValue = "admin" }
        };
        var claimQueryable = CreateMockQueryable(existingRecords);
        session.Query<DaliUserClaim>().Returns(claimQueryable);
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemoveClaimsAsync(user, [claimToRemove], CancellationToken.None);

        session.Received(1).Delete(existingRecords[0]);
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemoveClaimsAsync_ShouldBeNoOp_WhenNoMatchingClaims()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var claimToRemove = new Claim("role", "nonexistent");

        var claimQueryable = CreateMockQueryable(new List<DaliUserClaim>());
        session.Query<DaliUserClaim>().Returns(claimQueryable);
        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemoveClaimsAsync(user, [claimToRemove], CancellationToken.None);

        session.DidNotReceive().Delete(Arg.Any<DaliUserClaim>());
        // SaveChangesAsync is always called by the store even when no records match
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── GetUsersForClaimAsync ─────────────────────────────────────────

    [Test]
    public async Task GetUsersForClaimAsync_ShouldReturnUsers_WhenClaimMatches()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var claim = new Claim("role", "admin");

        var claimRecords = new List<DaliUserClaim>
        {
            new() { Id = "c1", UserId = "user-1", ClaimType = "role", ClaimValue = "admin" },
            new() { Id = "c2", UserId = "user-2", ClaimType = "role", ClaimValue = "admin" }
        };
        var claimQueryable = CreateMockQueryable(claimRecords);
        querySession.Query<DaliUserClaim>().Returns(claimQueryable);

        var user1 = new IdentityUser("alice") { Id = "user-1" };
        var user2 = new IdentityUser("bob") { Id = "user-2" };

        querySession.LoadAsync<IdentityUser>("user-1", Arg.Any<CancellationToken>()).Returns(user1);
        querySession.LoadAsync<IdentityUser>("user-2", Arg.Any<CancellationToken>()).Returns(user2);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetUsersForClaimAsync(claim, CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldContain(u => u.Id == "user-1");
        result.ShouldContain(u => u.Id == "user-2");
    }

    [Test]
    public async Task GetUsersForClaimAsync_ShouldReturnEmpty_WhenNoClaimMatches()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var claim = new Claim("role", "nonexistent");

        var claimQueryable = CreateMockQueryable(new List<DaliUserClaim>());
        querySession.Query<DaliUserClaim>().Returns(claimQueryable);

        var userStore = new DaliUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetUsersForClaimAsync(claim, CancellationToken.None);

        result.ShouldBeEmpty();
    }
}
