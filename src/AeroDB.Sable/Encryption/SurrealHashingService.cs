using SurrealDb.Net.Models.Response;

namespace AeroDB.Sable;

/// <summary>
/// Executes a closed, parameterized mapping of Sable hash algorithms to SurrealDB
/// crypto functions without routing protected values through Sable query logging.
/// </summary>
public sealed class SurrealHashingService : ISurrealHashingService
{
    private static readonly SurrealHashAlgorithm[] PasswordAlgorithms =
        [SurrealHashAlgorithm.Argon2, SurrealHashAlgorithm.Bcrypt,
         SurrealHashAlgorithm.Pbkdf2, SurrealHashAlgorithm.Scrypt];

    private static readonly SurrealHashAlgorithm[] DigestAlgorithms =
        [SurrealHashAlgorithm.Blake3, SurrealHashAlgorithm.Joaat,
         SurrealHashAlgorithm.Md5, SurrealHashAlgorithm.Sha1,
         SurrealHashAlgorithm.Sha256, SurrealHashAlgorithm.Sha512];

    private readonly InternalSessionBase _session;
    private SurrealHashCapabilities? _cachedCapabilities;

    public SurrealHashingService(IQuerySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session as InternalSessionBase
            ?? throw new SableHashingConfigurationException(
                "SurrealDB hashing requires an AeroDB.Sable-owned query session.");

        if (_session.StoreOptions.ClientFactory is not null
            && !_session.StoreOptions.Encryption.ExternalClientDisablesProtectedDataLogging)
        {
            throw new SableHashingConfigurationException(
                "ClientFactory supplies an external SurrealDB client. Set " +
                "StoreOptions.Encryption.ExternalClientDisablesProtectedDataLogging only after " +
                "disabling that client's query-value and CBOR serialization logging.");
        }

        ValidateTransport(_session.StoreOptions);
    }

    public async Task<SurrealPasswordHash> GenerateAsync(
        string plaintext,
        SurrealHashAlgorithm algorithm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var function = PasswordFunction(algorithm);
        var encoded = await ExecuteAsync<string>(
                $"RETURN crypto::{function}::generate($value);",
                new Dictionary<string, object?> { ["value"] = plaintext },
                algorithm,
                "generate",
                cancellationToken)
            .ConfigureAwait(false);
        return new SurrealPasswordHash(algorithm, encoded);
    }

    public Task<bool> VerifyAsync(
        string candidate,
        SurrealPasswordHash passwordHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(passwordHash);
        ArgumentNullException.ThrowIfNull(passwordHash.EncodedHash);
        var function = PasswordFunction(passwordHash.Algorithm);
        return ExecuteAsync<bool>(
            $"RETURN crypto::{function}::compare($hash, $candidate);",
            new Dictionary<string, object?>
            {
                ["hash"] = passwordHash.EncodedHash,
                ["candidate"] = candidate
            },
            passwordHash.Algorithm,
            "verify",
            cancellationToken);
    }

    public async Task<string> DigestAsync(
        string value,
        SurrealHashAlgorithm algorithm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var function = DigestFunction(algorithm);
        var query = $"RETURN crypto::{function}($value);";
        var parameters = new Dictionary<string, object?> { ["value"] = value };
        if (algorithm == SurrealHashAlgorithm.Joaat)
        {
            var number = await ExecuteAsync<long>(
                    query, parameters, algorithm, "digest", cancellationToken)
                .ConfigureAwait(false);
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return await ExecuteAsync<string>(
                query, parameters, algorithm, "digest", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SurrealHashCapabilities> ProbeCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cachedCapabilities is not null)
            return _cachedCapabilities;

        var supported = new List<SurrealHashAlgorithm>();
        var unsupported = new List<SurrealHashAlgorithm>();

        foreach (var algorithm in PasswordAlgorithms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = await GenerateAsync("aerodb-capability-probe", algorithm, cancellationToken)
                    .ConfigureAwait(false);
                supported.Add(algorithm);
            }
            catch (SableHashFunctionUnavailableException)
            {
                unsupported.Add(algorithm);
            }
        }

        foreach (var algorithm in DigestAlgorithms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = await DigestAsync("aerodb-capability-probe", algorithm, cancellationToken)
                    .ConfigureAwait(false);
                supported.Add(algorithm);
            }
            catch (SableHashFunctionUnavailableException)
            {
                unsupported.Add(algorithm);
            }
        }

        return _cachedCapabilities =
            new SurrealHashCapabilities(supported.AsReadOnly(), unsupported.AsReadOnly());
    }

    private async Task<T> ExecuteAsync<T>(
        string query,
        IReadOnlyDictionary<string, object?> parameters,
        SurrealHashAlgorithm algorithm,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            SurrealDbResponse response = await _session.Session
                .RawQuery(query, parameters, cancellationToken)
                .ConfigureAwait(false);
            if (response.HasErrors
                && response.Errors.OfType<SurrealDbErrorResult>()
                    .Any(error => IsUnknownFunction(error.Details)))
            {
                throw new SableHashFunctionUnavailableException(algorithm, operation);
            }
            response.EnsureAllOks();
            return response.GetValue<T>(0)
                ?? throw new InvalidOperationException("SurrealDB returned a null hash result.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SableHashingOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SableHashingOperationException(algorithm, operation, exception);
        }
    }

    private static string PasswordFunction(SurrealHashAlgorithm algorithm) => algorithm switch
    {
        SurrealHashAlgorithm.Argon2 => "argon2",
        SurrealHashAlgorithm.Bcrypt => "bcrypt",
        SurrealHashAlgorithm.Pbkdf2 => "pbkdf2",
        SurrealHashAlgorithm.Scrypt => "scrypt",
        _ when Enum.IsDefined(algorithm) => throw new SableHashingConfigurationException(
            $"Algorithm '{algorithm}' is a digest and cannot be used for password hashing."),
        _ => throw new SableHashingConfigurationException(
            $"Hash algorithm value '{(int)algorithm}' is not defined.")
    };

    private static string DigestFunction(SurrealHashAlgorithm algorithm) => algorithm switch
    {
        SurrealHashAlgorithm.Blake3 => "blake3",
        SurrealHashAlgorithm.Joaat => "joaat",
        SurrealHashAlgorithm.Md5 => "md5",
        SurrealHashAlgorithm.Sha1 => "sha1",
        SurrealHashAlgorithm.Sha256 => "sha256",
        SurrealHashAlgorithm.Sha512 => "sha512",
        _ when Enum.IsDefined(algorithm) => throw new SableHashingConfigurationException(
            $"Algorithm '{algorithm}' is a password hash and cannot be used as a digest."),
        _ => throw new SableHashingConfigurationException(
            $"Hash algorithm value '{(int)algorithm}' is not defined.")
    };

    private static bool IsUnknownFunction(string details) =>
        details.Contains("Invalid function/constant path", StringComparison.OrdinalIgnoreCase)
        || details.Contains("unknown function", StringComparison.OrdinalIgnoreCase)
        || details.Contains("function does not exist", StringComparison.OrdinalIgnoreCase);

    private static void ValidateTransport(StoreOptions options)
    {
        if (options.ClientFactory is not null)
        {
            if (!options.Encryption.ExternalClientUsesProtectedTransport)
            {
                throw new SableHashingConfigurationException(
                    "ClientFactory supplies an external SurrealDB client. Set " +
                    "StoreOptions.Encryption.ExternalClientUsesProtectedTransport only after " +
                    "confirming TLS or an in-process protected transport.");
            }
            return;
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new SableHashingConfigurationException(
                "The SurrealDB endpoint is not a valid absolute URI.");
        }
        if (endpoint.Scheme is "https" or "wss")
            return;
        if (endpoint.IsLoopback
            && endpoint.Scheme is "http" or "ws"
            && options.Encryption.AllowInsecureLoopbackHashing)
        {
            return;
        }

        throw new SableHashingConfigurationException(
            "Password hashing requires HTTPS/WSS. Insecure loopback transport requires " +
            "the explicit development-only AllowInsecureLoopbackHashing option.");
    }
}
