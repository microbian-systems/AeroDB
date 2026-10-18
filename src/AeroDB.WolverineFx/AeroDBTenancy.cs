using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using JasperFx.MultiTenancy;

namespace AeroDB.WolverineFx;

/// <summary>
/// Prevents tenant discovery from claiming Wolverine's main-store selectors.
/// These values are routed to the composite main store by Wolverine and can
/// never name a server-owned tenant database.
/// </summary>
internal static class AeroDBTenantSelector
{
    public static bool IsReserved(string? tenantId) =>
        string.Equals(tenantId, "main", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tenantId, "default", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Credential-free identity and scope of the coordinator's main database.
/// Tenant discovery must not route a customer to this database.
/// </summary>
internal sealed class AeroDBMainStoreIdentity
{
    private readonly string _namespace;
    private readonly string _database;

    public AeroDBMainStoreIdentity(string @namespace, string database)
    {
        _namespace = string.IsNullOrWhiteSpace(@namespace)
            ? throw new ArgumentException("The main message-store namespace is required.", nameof(@namespace))
            : @namespace;
        _database = string.IsNullOrWhiteSpace(database)
            ? throw new ArgumentException("The main message-store database is required.", nameof(database))
            : database;

        var location = new AeroDBTenantStoreLocation("main", "main", _namespace, _database);
        Uri = new Uri($"wolverinedb://aerodb/main-{location.PhysicalKey}");
    }

    public Uri Uri { get; }

    public bool Matches(AeroDBTenantStoreLocation location) =>
        string.Equals(_namespace, location.Namespace, StringComparison.Ordinal)
        && string.Equals(_database, location.Database, StringComparison.Ordinal);
}

/// <summary>
/// Immutable, server-owned location of a tenant's durable message storage.
/// This type deliberately excludes endpoints and credentials. Those remain the
/// responsibility of the server-side document-store factory.
/// </summary>
public sealed record AeroDBTenantStoreLocation
{
    private const int MaximumIdentifierUtf8Bytes = 255;
    private const int MaximumClusterAliasLength = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AeroDBTenantStoreLocation(string tenantId, string clusterAlias, string @namespace, string database)
    {
        TenantId = ServerOwnedIdentifier(tenantId, nameof(tenantId));
        ClusterAlias = ClusterAliasIdentifier(clusterAlias, nameof(clusterAlias));
        Namespace = ServerOwnedIdentifier(@namespace, nameof(@namespace));
        Database = ServerOwnedIdentifier(database, nameof(database));
        PhysicalKey = CreatePhysicalKey(ClusterAlias, Namespace, Database);
        PhysicalIdentity = new Uri($"wolverinedb://aerodb/{PhysicalKey}");
    }

    public string TenantId { get; }
    public string ClusterAlias { get; }
    public string Namespace { get; }
    public string Database { get; }

    /// <summary>
    /// Stable credential-free identity for the actual physical tenant database.
    /// It is intentionally independent of the tenant id so two tenant ids cannot
    /// accidentally acquire distinct message stores for one database.
    /// </summary>
    public string PhysicalKey { get; }

    public Uri PhysicalIdentity { get; }

    private static string ClusterAliasIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumClusterAliasLength
            || !IsAsciiIdentifier(value))
        {
            throw new ArgumentException(
                "The cluster alias must be a bounded ASCII identifier without credentials or endpoint syntax.",
                parameterName);
        }

        return value;
    }

    private static string ServerOwnedIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A tenant store location value is required.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("A tenant store location value cannot contain control characters.", parameterName);
        }

        int utf8ByteCount;
        try
        {
            utf8ByteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A tenant store location value must contain valid UTF-16 text.",
                parameterName,
                exception);
        }

        if (utf8ByteCount > MaximumIdentifierUtf8Bytes)
        {
            throw new ArgumentException(
                $"A tenant store location value cannot exceed {MaximumIdentifierUtf8Bytes} UTF-8 bytes.",
                parameterName);
        }

        return value;
    }

    private static bool IsAsciiIdentifier(string value)
    {
        if (value.Length == 0 || !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character =>
            IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-');
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';

    private static string CreatePhysicalKey(string clusterAlias, string @namespace, string database)
    {
        var values = new[] { clusterAlias, @namespace, database };
        var encoded = new List<byte>();
        Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];

        foreach (var value in values)
        {
            var bytes = StrictUtf8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, bytes.Length);
            encoded.AddRange(lengthPrefix);
            encoded.AddRange(bytes);
        }

        return Convert.ToHexString(SHA256.HashData(encoded.ToArray())).ToLowerInvariant();
    }
}

/// <summary>
/// Resolves server-owned tenant locations. A caller never supplies an endpoint,
/// namespace, database, or credential through this contract.
/// </summary>
public interface IAeroDBTenantStoreDirectory
{
    ValueTask<IReadOnlyList<AeroDBTenantStoreLocation>> ListActiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates an independent document store for one already validated location.
/// Server configuration supplies the endpoint and credentials.
/// Each returned store must have a fresh, independently owned client and fixed
/// namespace/database options. Neither store nor client may be shared between
/// calls. Initialization is performed by the tenant-pair factory; an implementation
/// must release its own partially created resources if it throws before returning.
/// </summary>
public interface IAeroDBTenantDocumentStoreFactory
{
    ValueTask<AeroDB.Sable.IDocumentStore> CreateAsync(
        AeroDBTenantStoreLocation location,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Immutable validated directory snapshot. Publishing a snapshot is all-or-nothing:
/// duplicate tenant ids and duplicate physical databases are rejected before use.
/// </summary>
internal sealed class AeroDBTenantStoreDirectorySnapshot
{
    private AeroDBTenantStoreDirectorySnapshot(
        ImmutableDictionary<string, AeroDBTenantStoreLocation> byTenant)
    {
        ByTenant = byTenant;
    }

    public ImmutableDictionary<string, AeroDBTenantStoreLocation> ByTenant { get; }

    public static AeroDBTenantStoreDirectorySnapshot Create(
        IEnumerable<AeroDBTenantStoreLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);

        var byTenant = ImmutableDictionary.CreateBuilder<string, AeroDBTenantStoreLocation>(StringComparer.Ordinal);
        var physicalKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var location in locations)
        {
            ArgumentNullException.ThrowIfNull(location);

            if (AeroDBTenantSelector.IsReserved(location.TenantId))
            {
                throw new InvalidOperationException(
                    $"Reserved Wolverine tenant selector '{location.TenantId}' cannot be published as a tenant location.");
            }

            if (!byTenant.TryAdd(location.TenantId, location))
            {
                throw new InvalidOperationException(
                    $"Tenant '{location.TenantId}' has more than one server-owned message-store location.");
            }

            if (!physicalKeys.Add(location.PhysicalKey))
            {
                throw new InvalidOperationException(
                    "Multiple tenant ids cannot map to the same physical message-store location.");
            }
        }

        return new AeroDBTenantStoreDirectorySnapshot(byTenant.ToImmutable());
    }
}

/// <summary>
/// Server-owned tenant-location publication boundary. The initial fail-closed
/// shape deliberately exposes no mutable cache and has no connection to runtime
/// message-store construction until its publication rules are proven.
/// </summary>
internal sealed class AeroDBTenantStoreRegistry
{
    internal sealed record StagedDirectory(AeroDBTenantStoreRegistry Owner,
        long Generation, AeroDBTenantStoreDirectorySnapshot Snapshot);

    private long _generation;
    private StagedDirectory? _latestStage;

    internal StagedDirectory Stage(IEnumerable<AeroDBTenantStoreLocation> locations)
    {
        var candidate = AeroDBTenantStoreDirectorySnapshot.Create(locations);
        lock (_publicationGate)
        {
            ValidateHistoricalAssignments(candidate);
            var generation = checked(_generation + 1);
            foreach (var location in candidate.ByTenant.Values)
            {
                _physicalByTenant[location.TenantId] = location.PhysicalKey;
                _tenantByPhysical[location.PhysicalKey] = location.TenantId;
            }
            _generation = generation;
            return _latestStage = new StagedDirectory(this, generation, candidate);
        }
    }

    internal AeroDBTenantStoreLocation FindStaged(StagedDirectory stage, string tenantId)
    {
        lock (_publicationGate)
        {
            ValidateStage(stage);
            return stage.Snapshot.ByTenant.TryGetValue(tenantId ?? string.Empty, out var location)
                ? location : throw new UnknownTenantIdException(tenantId ?? string.Empty);
        }
    }

    internal void Activate(StagedDirectory stage)
    {
        lock (_publicationGate)
        {
            ValidateStage(stage);
            _active = stage.Snapshot;
            _latestStage = null;
        }
    }

    private void ValidateStage(StagedDirectory stage)
    {
        // Reference identity also rejects copied or fabricated record tokens.
        if (stage is null || !ReferenceEquals(stage.Owner, this)
            || !ReferenceEquals(stage, _latestStage) || stage.Generation != _generation)
            throw new InvalidOperationException("The tenant-store stage is foreign, stale or already activated.");
    }

    private readonly object _publicationGate = new();
    private readonly Dictionary<string, string> _physicalByTenant = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tenantByPhysical = new(StringComparer.Ordinal);
    private AeroDBTenantStoreDirectorySnapshot _active =
        AeroDBTenantStoreDirectorySnapshot.Create(Array.Empty<AeroDBTenantStoreLocation>());

    public AeroDBTenantStoreLocation Find(string tenantId)
    {
        lock (_publicationGate)
        {
            if (_active.ByTenant.TryGetValue(tenantId ?? string.Empty, out var location))
            {
                return location;
            }
        }

        throw new UnknownTenantIdException(tenantId ?? string.Empty);
    }

    private void ValidateHistoricalAssignments(AeroDBTenantStoreDirectorySnapshot candidate)
    {
        foreach (var location in candidate.ByTenant.Values)
        {
            if (_physicalByTenant.TryGetValue(location.TenantId, out var knownPhysical)
                && !string.Equals(knownPhysical, location.PhysicalKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Tenant '{location.TenantId}' cannot be remapped to another physical message-store location.");
            }

            if (_tenantByPhysical.TryGetValue(location.PhysicalKey, out var knownTenant)
                && !string.Equals(knownTenant, location.TenantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A physical message-store location cannot be reused by another tenant.");
            }
        }
    }
}
