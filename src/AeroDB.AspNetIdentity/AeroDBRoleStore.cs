using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace AeroDB.AspNetIdentity;

/// <summary>
/// SurrealDB-backed store for <typeparamref name="TRole"/> roles,
/// implementing <see cref="IRoleStore{TRole}"/> and <see cref="IQueryableRoleStore{TRole}"/>.
/// </summary>
/// <typeparam name="TRole">The role type, must inherit from <see cref="IdentityRole"/>.</typeparam>
/// <typeparam name="TKey">The identity key type.</typeparam>
public class AeroDBRoleStore<TRole, TKey> : IRoleStore<TRole>, IQueryableRoleStore<TRole>
    where TRole : IdentityRole<TKey>
    where TKey : IEquatable<TKey>
{
    private readonly IDocumentStore _store;
    private readonly ILogger _logger;
    private readonly IdentityErrorDescriber _describer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="AeroDBRoleStore{TRole}"/>.
    /// </summary>
    /// <param name="store">The AeroDB document store.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="describer">Identity error describer.</param>
    public AeroDBRoleStore(
        IDocumentStore store,
        ILogger<AeroDBRoleStore<TRole, TKey>> logger,
        IdentityErrorDescriber? describer = null)
        : this(store, (ILogger)logger, describer)
    {
    }

    protected AeroDBRoleStore(
        IDocumentStore store,
        ILogger logger,
        IdentityErrorDescriber? describer = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _describer = describer ?? new IdentityErrorDescriber();
    }

    // ── IQueryableRoleStore ──────────────────────────────────────────

    /// <inheritdoc />
    public IQueryable<TRole> Roles
    {
        get
        {
            // IQueryableRoleStore.Roles is synchronous — AeroDB sessions are async-only.
            // Block on the async call to match the synchronous property contract.
            // This matches the Marten.AspNetIdentity pattern.
            var session = _store.QuerySessionAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return session.Query<TRole>();
        }
    }

    // ── IRoleStore ───────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IdentityResult> CreateAsync(TRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);

            if (!string.IsNullOrWhiteSpace(role.NormalizedName))
            {
                var duplicate = await session.Query<TRole>()
                    .FirstOrDefaultAsync(r => r.NormalizedName == role.NormalizedName, cancellationToken);
                if (duplicate is not null)
                    return IdentityResult.Failed(_describer.DuplicateRoleName(role.Name ?? role.NormalizedName));
            }

            session.Store(role);
            await session.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Created role {RoleName} ({RoleId})", role.Name, role.Id);
            return IdentityResult.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsUniqueConstraintViolation(ex))
                return IdentityResult.Failed(_describer.DuplicateRoleName(role.Name ?? role.NormalizedName ?? string.Empty));

            _logger.LogError(ex, "Failed to create the role {RoleName}.", role.Name);
            return IdentityResult.Failed(new IdentityError
            {
                Description = $"Failed to create the role '{role.Name}'."
            });
        }
    }

    /// <inheritdoc />
    public async Task<IdentityResult> UpdateAsync(TRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
            session.Store(role);
            await session.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Updated role {RoleName} ({RoleId})", role.Name, role.Id);
            return IdentityResult.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the role {RoleName}.", role.Name);
            return IdentityResult.Failed(new IdentityError
            {
                Description = $"Failed to update the role '{role.Name}'."
            });
        }
    }

    /// <inheritdoc />
    public async Task<IdentityResult> DeleteAsync(TRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
            session.Delete(role);
            await session.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Deleted role {RoleName} ({RoleId})", role.Name, role.Id);
            return IdentityResult.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete the role {RoleName}.", role.Name);
            return IdentityResult.Failed(new IdentityError
            {
                Description = $"Failed to delete the role '{role.Name}'."
            });
        }
    }

    /// <inheritdoc />
    public async Task<TRole?> FindByIdAsync(string roleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);
        return await session.LoadAsync<TRole>(roleId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TRole?> FindByNameAsync(string normalizedRoleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var session = await _store.QuerySessionAsync(cancellationToken);
        return await session.Query<TRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRoleName, cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> GetRoleIdAsync(TRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IdToString(role.Id));
    }

    /// <inheritdoc />
    public Task<string?> GetRoleNameAsync(TRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(role.Name);
    }

    /// <inheritdoc />
    public Task SetRoleNameAsync(TRole role, string? roleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        role.Name = roleName;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetNormalizedRoleNameAsync(TRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(role.NormalizedName);
    }

    /// <inheritdoc />
    public Task SetNormalizedRoleNameAsync(TRole role, string? normalizedName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        role.NormalizedName = normalizedName;
        return Task.CompletedTask;
    }

    // ── IDisposable ──────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // The store is DI-managed — no external resources to release.
    }

    private static string IdToString(TKey id)
        => Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool IsUniqueConstraintViolation(Exception ex)
        => ex.ToString().Contains("unique", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// String-key compatibility wrapper for the default ASP.NET Core Identity role type.
/// </summary>
public class AeroDBRoleStore<TRole> : AeroDBRoleStore<TRole, string>
    where TRole : IdentityRole
{
    public AeroDBRoleStore(
        IDocumentStore store,
        ILogger<AeroDBRoleStore<TRole>> logger,
        IdentityErrorDescriber? describer = null)
        : base(store, logger, describer)
    {
    }
}
