using System.Linq;
using System.Linq.Expressions;
using AeroDB;
using AeroDB.AspNetIdentity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace AeroDB.AspNetIdentity.Tests;

public class AeroDBUserStorePasskeyTests
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

    private static UserPasskeyInfo CreatePasskey(byte[]? credentialId = null)
    {
        return new UserPasskeyInfo(
            credentialId: credentialId ?? [1, 2, 3],
            publicKey: [4, 5, 6],
            createdAt: DateTimeOffset.UtcNow,
            signCount: 0,
            transports: ["internal"],
            isUserVerified: true,
            isBackupEligible: true,
            isBackedUp: false,
            attestationObject: [7, 8, 9],
            clientDataJson: [10, 11, 12])
        {
            Name = "test-passkey"
        };
    }

    private static AeroDBUserPasskey CreatePasskeyRecord(string userId, byte[]? credentialId = null)
    {
        return new AeroDBUserPasskey
        {
            Id = "pk-1",
            UserId = userId,
            CredentialId = credentialId ?? [1, 2, 3],
            PublicKey = [4, 5, 6],
            SignCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Transports = ["internal"],
            IsUserVerified = true,
            IsBackupEligible = true,
            IsBackedUp = false,
            AttestationObject = [7, 8, 9],
            ClientDataJson = [10, 11, 12],
            Name = "test-passkey"
        };
    }

    /// <summary>
    /// Helper used inside expression trees (Arg.Is predicate) to compare byte arrays.
    /// </summary>
    private static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    // ── GetPasskeysAsync ──────────────────────────────────────────────

    [Test]
    public async Task GetPasskeysAsync_ShouldReturnPasskeys_WhenPasskeysExist()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var records = new List<AeroDBUserPasskey>
        {
            CreatePasskeyRecord("user-1", [1, 2, 3]),
            CreatePasskeyRecord("user-1", [4, 5, 6])
        };
        records[1].Id = "pk-2";
        records[1].CredentialId = [4, 5, 6];

        var passkeyQueryable = CreateMockQueryable(records);
        querySession.Query<AeroDBUserPasskey>().Returns(passkeyQueryable);

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetPasskeysAsync(user, CancellationToken.None);

        var expected1 = new byte[] { 1, 2, 3 };
        var expected2 = new byte[] { 4, 5, 6 };
        result.Count.ShouldBe(2);
        result.ShouldContain(p => BytesEqual(p.CredentialId, expected1));
        result.ShouldContain(p => BytesEqual(p.CredentialId, expected2));
    }

    [Test]
    public async Task GetPasskeysAsync_ShouldReturnEmpty_WhenNoPasskeys()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };

        var passkeyQueryable = CreateMockQueryable(new List<AeroDBUserPasskey>());
        querySession.Query<AeroDBUserPasskey>().Returns(passkeyQueryable);

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.GetPasskeysAsync(user, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    // ── AddOrUpdatePasskeyAsync ────────────────────────────────────────

    [Test]
    public async Task AddOrUpdatePasskeyAsync_ShouldStoreNewPasskey_WhenNotExists()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var passkey = CreatePasskey();

        // FindPasskeyRecordAsync uses RawQueryAsync<AeroDBUserPasskey> — returns empty
        session.RawQueryAsync<AeroDBUserPasskey>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<AeroDBUserPasskey>());

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.AddOrUpdatePasskeyAsync(user, passkey, CancellationToken.None);

        session.Received(1).Store(Arg.Is<AeroDBUserPasskey>(r =>
            r.UserId == "user-1" &&
            BytesEqual(r.CredentialId, passkey.CredentialId) &&
            r.Name == "test-passkey"));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AddOrUpdatePasskeyAsync_ShouldUpdateExistingPasskey_WhenExists()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var passkey = CreatePasskey();

        var existingRecord = CreatePasskeyRecord("user-1", [1, 2, 3]);

        session.RawQueryAsync<AeroDBUserPasskey>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<AeroDBUserPasskey> { existingRecord });

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.AddOrUpdatePasskeyAsync(user, passkey, CancellationToken.None);

        // Existing record should be updated in place and stored
        session.Received(1).Store(Arg.Is<AeroDBUserPasskey>(r => r.Id == "pk-1"));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── FindPasskeyAsync ──────────────────────────────────────────────

    [Test]
    public async Task FindPasskeyAsync_ShouldReturnPasskey_WhenFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var credentialId = new byte[] { 1, 2, 3 };

        var passkeyRecord = CreatePasskeyRecord("user-1", credentialId);

        querySession.RawQueryAsync<AeroDBUserPasskey>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<AeroDBUserPasskey> { passkeyRecord });

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindPasskeyAsync(user, credentialId, CancellationToken.None);

        result.ShouldNotBeNull();
        BytesEqual(result.CredentialId, credentialId).ShouldBeTrue();
        result.Name.ShouldBe("test-passkey");
    }

    [Test]
    public async Task FindPasskeyAsync_ShouldReturnNull_WhenNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var credentialId = new byte[] { 99, 99, 99 };

        querySession.RawQueryAsync<AeroDBUserPasskey>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<AeroDBUserPasskey>());

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindPasskeyAsync(user, credentialId, CancellationToken.None);

        result.ShouldBeNull();
    }

    // ── FindByPasskeyIdAsync ──────────────────────────────────────────

    [Test]
    public async Task FindByPasskeyIdAsync_ShouldReturnUser_WhenPasskeyFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var credentialId = new byte[] { 1, 2, 3 };

        var passkeyRecord = new AeroDBUserPasskey
        {
            Id = "pk-1",
            UserId = "user-1",
            CredentialId = credentialId
        };

        querySession.RawQueryAsync<AeroDBUserPasskey>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<AeroDBUserPasskey> { passkeyRecord });

        var user = new IdentityUser("testuser") { Id = "user-1" };
        querySession.LoadAsync<IdentityUser>("user-1", Arg.Any<CancellationToken>()).Returns(user);

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindByPasskeyIdAsync(credentialId, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe("user-1");
    }

    [Test]
    public async Task FindByPasskeyIdAsync_ShouldReturnNull_WhenPasskeyNotFound()
    {
        var store = CreateStore(out var querySession, out _, out var logger);
        var credentialId = new byte[] { 99, 99, 99 };

        querySession.RawQueryAsync<AeroDBUserPasskey>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<AeroDBUserPasskey>());

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        var result = await userStore.FindByPasskeyIdAsync(credentialId, CancellationToken.None);

        result.ShouldBeNull();
    }

    // ── RemovePasskeyAsync ────────────────────────────────────────────

    [Test]
    public async Task RemovePasskeyAsync_ShouldDeletePasskey_WhenFound()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var credentialId = new byte[] { 1, 2, 3 };

        var passkeyRecord = CreatePasskeyRecord("user-1", credentialId);

        session.RawQueryAsync<AeroDBUserPasskey>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<AeroDBUserPasskey> { passkeyRecord });

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemovePasskeyAsync(user, credentialId, CancellationToken.None);

        session.Received(1).Delete(passkeyRecord);
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemovePasskeyAsync_ShouldNotDelete_WhenPasskeyNotFound()
    {
        var store = CreateStore(out _, out var session, out var logger);
        var user = new IdentityUser("testuser") { Id = "user-1" };
        var credentialId = new byte[] { 99, 99, 99 };

        session.RawQueryAsync<AeroDBUserPasskey>(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<AeroDBUserPasskey>());

        var userStore = new AeroDBUserStore<IdentityUser, IdentityRole>(store, logger);

        await userStore.RemovePasskeyAsync(user, credentialId, CancellationToken.None);

        session.DidNotReceive().Delete(Arg.Any<AeroDBUserPasskey>());
        await session.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
