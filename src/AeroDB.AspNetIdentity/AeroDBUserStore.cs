using System.Security.Claims;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace AeroDB.AspNetIdentity;

// ── Internal Model Types for Separate SurrealDB Tables ──────────────

/// <summary>
/// Represents a user claim stored in the <c>AeroDB_user_claim</c> SurrealDB table.
/// </summary>
internal sealed class AeroDBUserClaim
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ClaimType { get; set; } = string.Empty;
    public string ClaimValue { get; set; } = string.Empty;
}

/// <summary>
/// Represents an external login stored in the <c>AeroDB_user_login</c> SurrealDB table.
/// </summary>
internal sealed class AeroDBUserLogin
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string LoginProvider { get; set; } = string.Empty;
    public string ProviderKey { get; set; } = string.Empty;
    public string ProviderDisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Represents an authentication token stored in the <c>AeroDB_user_token</c> SurrealDB table.
/// </summary>
internal sealed class AeroDBUserToken
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string LoginProvider { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Represents a WebAuthn passkey stored in the <c>AeroDB_user_passkey</c> SurrealDB table.
/// </summary>
internal sealed class AeroDBUserPasskey
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];
    public uint SignCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string[] Transports { get; set; } = [];
    public bool IsUserVerified { get; set; }
    public bool IsBackupEligible { get; set; }
    public bool IsBackedUp { get; set; }
    public byte[] AttestationObject { get; set; } = [];
    public byte[] ClientDataJson { get; set; } = [];
    public byte[]? Aaguid { get; set; }
    public string? Name { get; set; }
}

// ── Main User Store ─────────────────────────────────────────────────

/// <summary>
/// SurrealDB-backed store for <typeparamref name="TUser"/> users,
/// implementing all standard ASP.NET Core Identity store interfaces including
/// passkey (WebAuthn) support for .NET 10.
/// </summary>
/// <typeparam name="TUser">The user type, must inherit from <see cref="IdentityUser"/>.</typeparam>
/// <typeparam name="TRole">The role type, must inherit from <see cref="IdentityRole"/>.</typeparam>
public class AeroDBUserStore<TUser, TRole> :
    IUserStore<TUser>,
    IUserPasswordStore<TUser>,
    IUserEmailStore<TUser>,
    IUserPhoneNumberStore<TUser>,
    IUserTwoFactorStore<TUser>,
    IUserAuthenticatorKeyStore<TUser>,
    IUserTwoFactorRecoveryCodeStore<TUser>,
    IQueryableUserStore<TUser>,
    IUserClaimStore<TUser>,
    IUserLoginStore<TUser>,
    IUserRoleStore<TUser>,
    IUserSecurityStampStore<TUser>,
    IUserLockoutStore<TUser>,
    IUserAuthenticationTokenStore<TUser>,
    IUserPasskeyStore<TUser>,
    IProtectedUserStore<TUser>
    where TUser : IdentityUser
    where TRole : IdentityRole
{
    private readonly IDocumentStore _store;
    private readonly ILogger<AeroDBUserStore<TUser, TRole>> _logger;
    private bool _disposed;

    // Cached snake_case table names computed from the CLR type names.
    private readonly string _userTable;
    private readonly string _roleTable;
    private readonly string _claimTable;
    private readonly string _loginTable;
    private readonly string _tokenTable;
    private readonly string _passkeyTable;

    /// <summary>
    /// Initializes a new instance of <see cref="AeroDBUserStore{TUser, TRole}"/>.
    /// </summary>
    /// <param name="store">The AeroDB document store.</param>
    /// <param name="logger">Logger instance.</param>
    public AeroDBUserStore(IDocumentStore store, ILogger<AeroDBUserStore<TUser, TRole>> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _userTable = ToSnakeCase(typeof(TUser).Name);
        _roleTable = ToSnakeCase(typeof(TRole).Name);
        _claimTable = ToSnakeCase(typeof(AeroDBUserClaim).Name);
        _loginTable = ToSnakeCase(typeof(AeroDBUserLogin).Name);
        _tokenTable = ToSnakeCase(typeof(AeroDBUserToken).Name);
        _passkeyTable = ToSnakeCase(typeof(AeroDBUserPasskey).Name);
    }

    // ══════════════════════════════════════════════════════════════════
    //  IQueryableUserStore
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public IQueryable<TUser> Users
    {
        get
        {
            // IQueryableUserStore.Users is synchronous — AeroDB sessions are async-only.
            // Block on the async call to match the synchronous property contract.
            var session = _store.QuerySessionAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return session.Query<TUser>();
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserStore — Core CRUD
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<IdentityResult> CreateAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
            session.Store(user);
            await session.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Created user {UserId}", user.Id);
            return IdentityResult.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create the user {UserId}.", user.Id);
            return IdentityResult.Failed(new IdentityError
            {
                Description = $"Failed to create the user '{user.UserName}'."
            });
        }
    }

    /// <inheritdoc />
    public async Task<IdentityResult> UpdateAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
            session.Store(user);
            await session.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Updated user {UserId}", user.Id);
            return IdentityResult.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the user {UserId}.", user.Id);
            return IdentityResult.Failed(new IdentityError
            {
                Description = $"Failed to update the user '{user.UserName}'."
            });
        }
    }

    /// <inheritdoc />
    public async Task<IdentityResult> DeleteAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

            // Remove associated records from separate tables
            await DeleteAssociatedRecordsAsync(session, user.Id, cancellationToken);

            // Remove the user document
            session.Delete(user);
            await session.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Deleted user {UserId}", user.Id);
            return IdentityResult.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete the user {UserId}.", user.Id);
            return IdentityResult.Failed(new IdentityError
            {
                Description = $"Failed to delete the user '{user.UserName}'."
            });
        }
    }

    /// <inheritdoc />
    public async Task<TUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);
        return await session.LoadAsync<TUser>(userId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);
        return await session.Query<TUser>()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName, cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> GetUserIdAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.Id);
    }

    /// <inheritdoc />
    public Task<string?> GetUserNameAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(user.UserName);
    }

    /// <inheritdoc />
    public Task SetUserNameAsync(TUser user, string? userName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.UserName = userName;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetNormalizedUserNameAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(user.NormalizedUserName);
    }

    /// <inheritdoc />
    public Task SetNormalizedUserNameAsync(TUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserPasswordStore
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<string?> GetPasswordHashAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(user.PasswordHash);
    }

    /// <inheritdoc />
    public Task SetPasswordHashAsync(TUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> HasPasswordAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserEmailStore
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<string?> GetEmailAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.Email);
    }

    /// <inheritdoc />
    public Task SetEmailAsync(TUser user, string? email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.Email = email;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> GetEmailConfirmedAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.EmailConfirmed);
    }

    /// <inheritdoc />
    public Task SetEmailConfirmedAsync(TUser user, bool confirmed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<TUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);
        return await session.Query<TUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
    }

    /// <inheritdoc />
    public Task<string?> GetNormalizedEmailAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.NormalizedEmail);
    }

    /// <inheritdoc />
    public Task SetNormalizedEmailAsync(TUser user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserPhoneNumberStore
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<string?> GetPhoneNumberAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.PhoneNumber);
    }

    /// <inheritdoc />
    public Task SetPhoneNumberAsync(TUser user, string? phoneNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.PhoneNumber = phoneNumber;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> GetPhoneNumberConfirmedAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.PhoneNumberConfirmed);
    }

    /// <inheritdoc />
    public Task SetPhoneNumberConfirmedAsync(TUser user, bool confirmed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.PhoneNumberConfirmed = confirmed;
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserTwoFactorStore
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<bool> GetTwoFactorEnabledAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.TwoFactorEnabled);
    }

    /// <inheritdoc />
    public Task SetTwoFactorEnabledAsync(TUser user, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserSecurityStampStore
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<string?> GetSecurityStampAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.SecurityStamp);
    }

    /// <inheritdoc />
    public Task SetSecurityStampAsync(TUser user, string? stamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserLockoutStore
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<DateTimeOffset?> GetLockoutEndDateAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.LockoutEnd);
    }

    /// <inheritdoc />
    public Task SetLockoutEndDateAsync(TUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> IncrementAccessFailedCountAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.AccessFailedCount++;
        return Task.FromResult(user.AccessFailedCount);
    }

    /// <inheritdoc />
    public Task ResetAccessFailedCountAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> GetAccessFailedCountAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.AccessFailedCount);
    }

    /// <inheritdoc />
    public Task<bool> GetLockoutEnabledAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(user.LockoutEnabled);
    }

    /// <inheritdoc />
    public Task SetLockoutEnabledAsync(TUser user, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserAuthenticatorKeyStore  (persisted via raw SurrealQL)
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<string?> GetAuthenticatorKeyAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Raw SQL: reads a single embedded field without loading the full user document.
        await using var session = await _store.QuerySessionAsync(cancellationToken);
        var results = await session.RawQueryAsync<AuthenticatorKeyResult>(
            $"SELECT authenticator_key FROM {_userTable}:{user.Id}",
            parameters: null,
            cancellationToken);
        return results.FirstOrDefault()?.AuthenticatorKey;
    }

    /// <inheritdoc />
    public async Task SetAuthenticatorKeyAsync(TUser user, string? key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
        // Raw SQL: updates a single embedded field without loading the full user document.
        await session.ExecuteSqlAsync(
            $"UPDATE {_userTable}:{user.Id} SET authenticator_key = $key",
            new Dictionary<string, object?> { ["key"] = key },
            cancellationToken);
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserTwoFactorRecoveryCodeStore  (persisted via raw SurrealQL)
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<bool> RedeemCodeAsync(TUser user, string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

        var codes = await GetRecoveryCodesAsync(session, user.Id, cancellationToken);
        var match = codes.FirstOrDefault(c => c == code);
        if (match is null)
            return false;

        codes.Remove(match);
        await SetRecoveryCodesAsync(session, user.Id, codes, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> CountCodesAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);
        var codes = await GetRecoveryCodesAsync(session, user.Id, cancellationToken);
        return codes.Count;
    }

    /// <inheritdoc />
    public async Task ReplaceCodesAsync(TUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
        await SetRecoveryCodesAsync(session, user.Id, recoveryCodes.ToList(), cancellationToken);
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserClaimStore
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<IList<Claim>> GetClaimsAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);
        var records = await session.Query<AeroDBUserClaim>()
            .Where(c => c.UserId == user.Id)
            .ToListAsync(cancellationToken);
        return records.Select(ToClaim).ToList();
    }

    /// <inheritdoc />
    public async Task AddClaimsAsync(TUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
        foreach (var claim in claims)
        {
            session.Store(new AeroDBUserClaim
            {
                UserId = user.Id,
                ClaimType = claim.Type,
                ClaimValue = claim.Value
            });
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReplaceClaimAsync(TUser user, Claim claim, Claim newClaim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

        var records = await session.Query<AeroDBUserClaim>()
            .Where(c => c.UserId == user.Id
                     && c.ClaimType == claim.Type
                     && c.ClaimValue == claim.Value)
            .ToListAsync(cancellationToken);

        foreach (var record in records)
        {
            record.ClaimType = newClaim.Type;
            record.ClaimValue = newClaim.Value;
            session.Store(record);
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveClaimsAsync(TUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

        foreach (var claim in claims)
        {
            var records = await session.Query<AeroDBUserClaim>()
                .Where(c => c.UserId == user.Id
                         && c.ClaimType == claim.Type
                         && c.ClaimValue == claim.Value)
                .ToListAsync(cancellationToken);

            foreach (var record in records)
                session.Delete(record);
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IList<TUser>> GetUsersForClaimAsync(Claim claim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);

        var claimRecords = await session.Query<AeroDBUserClaim>()
            .Where(c => c.ClaimType == claim.Type && c.ClaimValue == claim.Value)
            .ToListAsync(cancellationToken);

        var userIds = claimRecords.Select(c => c.UserId).Distinct().ToList();
        var users = new List<TUser>();

        foreach (var userId in userIds)
        {
            var user = await session.LoadAsync<TUser>(userId, cancellationToken);
            if (user is not null)
                users.Add(user);
        }

        return users;
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserLoginStore
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddLoginAsync(TUser user, UserLoginInfo login, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
        session.Store(new AeroDBUserLogin
        {
            UserId = user.Id,
            LoginProvider = login.LoginProvider,
            ProviderKey = login.ProviderKey,
            ProviderDisplayName = login.ProviderDisplayName ?? string.Empty
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveLoginAsync(TUser user, string loginProvider, string providerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

        var record = await session.Query<AeroDBUserLogin>()
            .FirstOrDefaultAsync(l =>
                l.UserId == user.Id &&
                l.LoginProvider == loginProvider &&
                l.ProviderKey == providerKey,
                cancellationToken);

        if (record is not null)
        {
            session.Delete(record);
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IList<UserLoginInfo>> GetLoginsAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);

        var records = await session.Query<AeroDBUserLogin>()
            .Where(l => l.UserId == user.Id)
            .ToListAsync(cancellationToken);

        return records
            .Select(r => new UserLoginInfo(r.LoginProvider, r.ProviderKey, r.ProviderDisplayName))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<TUser?> FindByLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);

        var loginRecord = await session.Query<AeroDBUserLogin>()
            .FirstOrDefaultAsync(l =>
                l.LoginProvider == loginProvider &&
                l.ProviderKey == providerKey,
                cancellationToken);

        if (loginRecord is null)
            return null;

        return await session.LoadAsync<TUser>(loginRecord.UserId, cancellationToken);
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserRoleStore   (role IDs stored as embedded array via raw SQL)
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task AddToRoleAsync(TUser user, string normalizedRoleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

        var role = await session.Query<TRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRoleName, cancellationToken);

        if (role is null)
            return;

        var roleIds = await GetRoleIdsAsync(session, user.Id, cancellationToken);
        if (roleIds.Contains(role.Id))
            return;

        roleIds.Add(role.Id);
        await SetRoleIdsAsync(session, user.Id, roleIds, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveFromRoleAsync(TUser user, string normalizedRoleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

        var role = await session.Query<TRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRoleName, cancellationToken);

        if (role is null)
            return;

        var roleIds = await GetRoleIdsAsync(session, user.Id, cancellationToken);
        if (!roleIds.Remove(role.Id))
            return;

        await SetRoleIdsAsync(session, user.Id, roleIds, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IList<string>> GetRolesAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);

        var roleIds = await GetRoleIdsAsync(session, user.Id, cancellationToken);
        if (roleIds.Count == 0)
            return Array.Empty<string>();

        var roles = await session.Query<TRole>()
            .Where(r => roleIds.Contains(r.Id!))
            .ToListAsync(cancellationToken);

        return roles.Select(r => r.Name!).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> IsInRoleAsync(TUser user, string normalizedRoleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);

        var role = await session.Query<TRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRoleName, cancellationToken);

        if (role is null)
            return false;

        var roleIds = await GetRoleIdsAsync(session, user.Id, cancellationToken);
        return roleIds.Contains(role.Id);
    }

    /// <inheritdoc />
    public async Task<IList<TUser>> GetUsersInRoleAsync(string normalizedRoleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);

        var role = await session.Query<TRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRoleName, cancellationToken);

        if (role is null)
            return Array.Empty<TUser>();

        // Raw SQL: SurrealDB CONTAINS operator has no LINQ equivalent in AeroDB's fluent API.
        return await session.RawQueryAsync<TUser>(
            $"SELECT * FROM {_userTable} WHERE role_ids CONTAINS $roleId",
            new Dictionary<string, object?> { ["roleId"] = role.Id },
            cancellationToken);
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserAuthenticationTokenStore   (separate user_token table)
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task SetTokenAsync(TUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

        var existing = await session.Query<AeroDBUserToken>()
            .FirstOrDefaultAsync(t =>
                t.UserId == user.Id &&
                t.LoginProvider == loginProvider &&
                t.Name == name,
                cancellationToken);

        if (existing is not null)
        {
            existing.Value = value ?? string.Empty;
            session.Store(existing);
        }
        else
        {
            session.Store(new AeroDBUserToken
            {
                UserId = user.Id,
                LoginProvider = loginProvider,
                Name = name,
                Value = value ?? string.Empty
            });
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveTokenAsync(TUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

        var existing = await session.Query<AeroDBUserToken>()
            .FirstOrDefaultAsync(t =>
                t.UserId == user.Id &&
                t.LoginProvider == loginProvider &&
                t.Name == name,
                cancellationToken);

        if (existing is not null)
        {
            session.Delete(existing);
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetTokenAsync(TUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);

        var token = await session.Query<AeroDBUserToken>()
            .FirstOrDefaultAsync(t =>
                t.UserId == user.Id &&
                t.LoginProvider == loginProvider &&
                t.Name == name,
                cancellationToken);

        return token?.Value;
    }

    // ══════════════════════════════════════════════════════════════════
    //  IUserPasskeyStore   (separate user_passkey table)
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);

        var records = await session.Query<AeroDBUserPasskey>()
            .Where(p => p.UserId == user.Id)
            .ToListAsync(cancellationToken);

        return records.Select(ToPasskeyInfo).ToList();
    }

    /// <inheritdoc />
    public async Task AddOrUpdatePasskeyAsync(TUser user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

        // Try to find an existing passkey with the same credential ID for this user.
        var existing = await FindPasskeyRecordAsync(session, user.Id, passkey.CredentialId, cancellationToken);

        if (existing is not null)
        {
            // Update in place
            existing.PublicKey = passkey.PublicKey;
            existing.SignCount = passkey.SignCount;
            existing.Transports = passkey.Transports ?? [];
            existing.IsUserVerified = passkey.IsUserVerified;
            existing.IsBackupEligible = passkey.IsBackupEligible;
            existing.IsBackedUp = passkey.IsBackedUp;
            existing.AttestationObject = passkey.AttestationObject;
            existing.ClientDataJson = passkey.ClientDataJson;
            existing.Name = passkey.Name;
            session.Store(existing);
        }
        else
        {
            session.Store(ToPasskeyRecord(user.Id, passkey));
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UserPasskeyInfo?> FindPasskeyAsync(TUser user, byte[] credentialId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);

        var record = await FindPasskeyRecordAsync(session, user.Id, credentialId, cancellationToken);
        return record is not null ? ToPasskeyInfo(record) : null;
    }

    /// <inheritdoc />
    public async Task<TUser?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);

        // Raw SQL: byte[] credential ID comparison is not reliably supported in LINQ
        // across SurrealDB drivers. See FindPasskeyRecordAsync for detailed comment.
        var passkeys = await session.RawQueryAsync<AeroDBUserPasskey>(
            $"SELECT * FROM {_passkeyTable} WHERE credential_id = $credentialId LIMIT 1",
            new Dictionary<string, object?> { ["credentialId"] = credentialId },
            cancellationToken);

        var passkey = passkeys.FirstOrDefault();
        if (passkey is null)
            return null;

        return await session.LoadAsync<TUser>(passkey.UserId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemovePasskeyAsync(TUser user, byte[] credentialId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

        // Find the passkey record by credential ID for this user.
        var passkey = await FindPasskeyRecordAsync(session, user.Id, credentialId, cancellationToken);
        if (passkey is not null)
        {
            session.Delete(passkey);
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  IDisposable
    // ══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // The store is DI-managed — no external resources to release.
    }

    // ══════════════════════════════════════════════════════════════════
    //  Private Helpers — Recovery Codes (embedded in user doc)
    // ══════════════════════════════════════════════════════════════════

    private async Task<List<string>> GetRecoveryCodesAsync(IQuerySession session, string userId, CancellationToken ct)
    {
        // Raw SQL: reads a single embedded field without loading the full user document.
        var result = await session.RawQueryAsync<RecoveryCodesResult>(
            $"SELECT recovery_codes FROM {_userTable}:{userId}",
            parameters: null,
            ct);
        return result.FirstOrDefault()?.RecoveryCodes ?? [];
    }

    private async Task SetRecoveryCodesAsync(IDocumentSession session, string userId, List<string> codes, CancellationToken ct)
    {
        // Raw SQL: updates a single embedded field without loading the full user document.
        await session.ExecuteSqlAsync(
            $"UPDATE {_userTable}:{userId} SET recovery_codes = $codes",
            new Dictionary<string, object?> { ["codes"] = codes },
            ct);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Private Helpers — Role IDs (embedded array in user doc)
    // ══════════════════════════════════════════════════════════════════

    private async Task<List<string>> GetRoleIdsAsync(IQuerySession session, string userId, CancellationToken ct)
    {
        // Raw SQL: reads a single embedded field without loading the full user document.
        var result = await session.RawQueryAsync<RoleIdsResult>(
            $"SELECT role_ids FROM {_userTable}:{userId}",
            parameters: null,
            ct);
        return result.FirstOrDefault()?.RoleIds ?? [];
    }

    private async Task SetRoleIdsAsync(IDocumentSession session, string userId, List<string> roleIds, CancellationToken ct)
    {
        // Raw SQL: updates an embedded array field without loading the full user document.
        if (roleIds.Count == 0)
        {
            await session.ExecuteSqlAsync(
                $"UPDATE {_userTable}:{userId} SET role_ids = NONE",
                parameters: null,
                ct);
        }
        else
        {
            await session.ExecuteSqlAsync(
                $"UPDATE {_userTable}:{userId} SET role_ids = $roleIds",
                new Dictionary<string, object?> { ["roleIds"] = roleIds },
                ct);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  Private Helpers — Passkey (separate table, byte[] comparisons)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Finds a passkey record for the given user and credential ID using raw SQL
    /// (byte[] comparison is not reliably supported via LINQ across all SurrealDB drivers).
    /// </summary>
    private async Task<AeroDBUserPasskey?> FindPasskeyRecordAsync(IQuerySession session, string userId, byte[] credentialId, CancellationToken ct)
    {
        var passkeys = await session.RawQueryAsync<AeroDBUserPasskey>(
            $"SELECT * FROM {_passkeyTable} WHERE user_id = $userId AND credential_id = $credentialId LIMIT 1",
            new Dictionary<string, object?>
            {
                ["userId"] = userId,
                ["credentialId"] = credentialId
            },
            ct);
        return passkeys.FirstOrDefault();
    }

    // ══════════════════════════════════════════════════════════════════
    //  Private Helpers — Delete Associated Records
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads and deletes all associated claim, login, token, and passkey records
    /// for the given user ID using the AeroDB session pattern.
    /// </summary>
    private async Task DeleteAssociatedRecordsAsync(IDocumentSession session, string userId, CancellationToken ct)
    {
        // Claims
        var claims = await session.Query<AeroDBUserClaim>()
            .Where(c => c.UserId == userId)
            .ToListAsync(ct);
        foreach (var c in claims)
            session.Delete(c);

        // Logins
        var logins = await session.Query<AeroDBUserLogin>()
            .Where(l => l.UserId == userId)
            .ToListAsync(ct);
        foreach (var l in logins)
            session.Delete(l);

        // Tokens
        var tokens = await session.Query<AeroDBUserToken>()
            .Where(t => t.UserId == userId)
            .ToListAsync(ct);
        foreach (var t in tokens)
            session.Delete(t);

        // Passkeys
        var passkeys = await session.Query<AeroDBUserPasskey>()
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);
        foreach (var p in passkeys)
            session.Delete(p);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Private Conversion Helpers
    // ══════════════════════════════════════════════════════════════════

    private static Claim ToClaim(AeroDBUserClaim record)
        => new(record.ClaimType, record.ClaimValue);

    private static AeroDBUserPasskey ToPasskeyRecord(string userId, UserPasskeyInfo info)
        => new()
        {
            UserId = userId,
            CredentialId = info.CredentialId,
            PublicKey = info.PublicKey,
            SignCount = info.SignCount,
            CreatedAt = info.CreatedAt,
            Transports = info.Transports ?? [],
            IsUserVerified = info.IsUserVerified,
            IsBackupEligible = info.IsBackupEligible,
            IsBackedUp = info.IsBackedUp,
            AttestationObject = info.AttestationObject,
            ClientDataJson = info.ClientDataJson,
            Name = info.Name
        };

    private static UserPasskeyInfo ToPasskeyInfo(AeroDBUserPasskey record)
        => new(
            credentialId: record.CredentialId,
            publicKey: record.PublicKey,
            createdAt: record.CreatedAt,
            signCount: record.SignCount,
            transports: record.Transports,
            isUserVerified: record.IsUserVerified,
            isBackupEligible: record.IsBackupEligible,
            isBackedUp: record.IsBackedUp,
            attestationObject: record.AttestationObject,
            clientDataJson: record.ClientDataJson)
        {
            Name = record.Name
        };

    /// <summary>
    /// Converts a PascalCase or camelCase name to snake_case.
    /// Matches AeroDB's default naming convention for SurrealDB tables.
    /// </summary>
    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c)
                ? "_" + char.ToLowerInvariant(c)
                : char.ToLowerInvariant(c).ToString()));
    }

}

// ══════════════════════════════════════════════════════════════════
//  Raw-SQL Result Types
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// Result shape for SELECT authenticator_key FROM user:{id}.
/// </summary>
internal sealed class AuthenticatorKeyResult
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Local
    public string? AuthenticatorKey { get; set; }
}

/// <summary>
/// Result shape for SELECT recovery_codes FROM user:{id}.
/// </summary>
internal sealed class RecoveryCodesResult
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Local
    public List<string>? RecoveryCodes { get; set; }
}

/// <summary>
/// Result shape for SELECT role_ids FROM user:{id}.
/// </summary>
internal sealed class RoleIdsResult
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Local
    public List<string>? RoleIds { get; set; }
}
