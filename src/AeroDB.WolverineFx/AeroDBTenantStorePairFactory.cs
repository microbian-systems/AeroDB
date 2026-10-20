using AeroDB.Sable;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using SurrealDb.Net;

namespace AeroDB.WolverineFx;

/// <summary>
/// Constructs fixed, independently owned tenant pairs. This component remains
/// unregistered; Wolverine, not discovery, owns message-schema migration.
/// </summary>
internal sealed class AeroDBTenantStorePairFactory : IAeroDBTenantStorePairFactory
{
    private readonly IAeroDBTenantDocumentStoreFactory _documentStores;
    private readonly ILogger<AeroDBMessageStore> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly AeroDBRuntimeNodeIdentity _runtimeIdentity;
    private readonly ConcurrentDictionary<string, string> _endpoints = new(StringComparer.Ordinal);
    private readonly object _ownershipGate = new();
    private readonly HashSet<IDocumentStore> _stores = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ISurrealDbClient> _clients = new(ReferenceEqualityComparer.Instance);

    public AeroDBTenantStorePairFactory(
        IAeroDBTenantDocumentStoreFactory documentStores,
        ILogger<AeroDBMessageStore> logger,
        ILoggerFactory? loggerFactory = null,
        AeroDBRuntimeNodeIdentity? runtimeIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(documentStores);
        ArgumentNullException.ThrowIfNull(logger);
        _documentStores = documentStores;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _runtimeIdentity = runtimeIdentity ?? new AeroDBRuntimeNodeIdentity();
    }

    public async ValueTask<IAeroDBTenantStorePair> CreateAsync(
        AeroDBTenantStoreLocation location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        cancellationToken.ThrowIfCancellationRequested();
        var document = await _documentStores.CreateAsync(location, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The tenant document-store factory returned no store.");

        lock (_ownershipGate)
        {
            if (!_stores.Add(document))
                throw new InvalidOperationException("The tenant document-store factory reused an owned store.");
        }

        ISurrealDbClient? ownedClient = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = ValidateScope(document.Options, location);
            var knownEndpoint = _endpoints.GetOrAdd(location.ClusterAlias, endpoint);
            if (!string.Equals(knownEndpoint, endpoint, StringComparison.Ordinal))
                throw new InvalidOperationException("A server-owned cluster alias cannot be remapped.");

            await document.InitializeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var client = document.Client
                ?? throw new InvalidOperationException("The initialized tenant document store has no client.");
            lock (_ownershipGate)
            {
                if (!_clients.Add(client))
                    throw new InvalidOperationException("The tenant document-store factory reused an owned client.");
                ownedClient = client;
            }

            if (!string.Equals(endpoint, ValidateScope(document.Options, location), StringComparison.Ordinal))
                throw new InvalidOperationException("The tenant document-store endpoint changed during initialization.");

            var messages = new AeroDBMessageStore(client, _logger, _loggerFactory,
                document.Options, ownsClient: false, storeIdentity: location.PhysicalIdentity,
                runtimeIdentity: _runtimeIdentity);
            messages.DemoteToAncillary();
            messages.TenantIds.Add(location.TenantId);
            return new AeroDBTenantStorePair(location, document, messages);
        }
        catch (Exception originalFailure)
        {
            try
            {
                // A contract-violating wrapper can point at another pair's
                // client even when its own scope validation failed. Never let
                // cleanup dispose that healthy tenant's resources.
                if (!HasForeignOwnedClient(document, ownedClient))
                    await document.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Tenant-store creation and cleanup both failed.",
                    originalFailure, cleanupFailure);
            }
            finally
            {
                lock (_ownershipGate)
                {
                    _stores.Remove(document);
                    if (ownedClient is not null) _clients.Remove(ownedClient);
                }
            }

            throw;
        }
    }

    private bool HasForeignOwnedClient(IDocumentStore document, ISurrealDbClient? ownedClient)
    {
        ISurrealDbClient client;
        try { client = document.Client; }
        catch (InvalidOperationException) { return false; } // Sable's uninitialized-store contract.
        lock (_ownershipGate)
            return _clients.Contains(client) && !ReferenceEquals(client, ownedClient);
    }

    private static string ValidateScope(StoreOptions options, AeroDBTenantStoreLocation location)
    {
        if (options is null
            || !string.Equals(options.Namespace, location.Namespace, StringComparison.Ordinal)
            || !string.Equals(options.Database, location.Database, StringComparison.Ordinal)
            || options.TenancyStyle is not (TenancyStyle.Single or TenancyStyle.None)
            || !string.IsNullOrEmpty(options.DefaultTenantId))
            throw new InvalidOperationException("A tenant document store must have the exact fixed server-owned scope.");

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("A tenant document-store endpoint must be absolute and credential-free.");

        return endpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }
}
