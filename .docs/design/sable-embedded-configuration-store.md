# AeroDB.Sable.Configuration — Embedded Encrypted Configuration Store

> **Status:** V1 implemented and verified
> **Date:** 2026-07-19
> **Package:** `AeroDB.Sable.Configuration`
> **Scope:** In-process, persistent SurrealKV configuration storage with application-layer encrypted values

---

## 1. Purpose and boundary

`AeroDB.Sable.Configuration` is a separate package that supplies an encrypted,
persistent configuration source backed by embedded SurrealDB SurrealKV.

It is intentionally **not** `AeroDB.Sable.Vault`.

The package protects configuration values from disclosure through copied
SurrealKV files, disk snapshots, and database-only access. It does not protect
values or keys from a compromised application process because the application
loads both the plaintext configuration and the wrapping-key provider.

The `AeroDB.Sable.Vault` service now lives in the separate
[AeroVault repository](https://github.com/microbian-systems/AeroVault). It
remains a separate process and security boundary with workload authentication,
authorization policies, audit, secret versioning, and remote transit encryption.

## 2. V1 goals

- Ship as the separate `AeroDB.Sable.Configuration` NuGet package.
- Use persistent embedded SurrealKV, never an in-memory provider by default.
- Integrate with the standard .NET `IConfigurationSource` and
  `ConfigurationProvider` contracts.
- Load all values into a case-insensitive startup snapshot.
- Encrypt every stored value with Sable envelope encryption.
- Use a fresh per-write DEK and nonce through
  `ISableDataProtectionProvider`.
- Keep the KEK outside SurrealDB and require explicit bootstrap configuration.
- Expose an async CRUD store for provisioning and administration.
- Support explicit asynchronous refresh with an `IChangeToken` notification.
- Fail closed on missing bootstrap data, malformed envelopes, wrong keys,
  duplicate logical keys, and decryption failures.

## 3. Explicit non-goals

V1 does not provide:

- a network service or remote SurrealDB connection;
- OIDC, Entra ID, Keycloak, bearer tokens, or mTLS;
- Vault policies, tenants, leases, audit events, or secret version history;
- live-query or polling refresh;
- persistence through `IConfiguration`'s synchronous indexer;
- automatic KEK creation, recovery, escrow, or rotation;
- protection from application compromise, debuggers, or process-memory access.

## 4. Public API

```csharp
builder.Configuration.AddSableConfiguration(options =>
{
    options.DatabasePath = "/var/lib/my-app/sable-configuration";
    options.UseMountedKeyFile(
        "/run/secrets/sable-configuration-kek",
        keyId: "config-kek-2026-07");
});
```

The configuration provider is read-only. Configuration writes use the
asynchronous store:

```csharp
var options = new SableConfigurationOptions
{
    DatabasePath = "/var/lib/my-app/sable-configuration"
};
options.UseMountedKeyFile(
    "/run/secrets/sable-configuration-kek",
    keyId: "config-kek-2026-07");

using var store = new SableConfigurationStore(options);
await store.SetAsync("ConnectionStrings:Primary", connectionString);
```

`ConfigurationProvider.Set(...)` throws a specific exception that directs the
caller to `ISableConfigurationStore.SetAsync(...)`. A synchronous
configuration API must not disguise database I/O or create a non-persistent
in-memory override.

### 4.1 Standard .NET consumption

The provider deliberately produces the same flattened key/value model as JSON,
environment variables, command-line arguments, and User Secrets. For example,
the JSON path `Payments.ApiKey` becomes the configuration key
`Payments:ApiKey`. Consumers therefore remain provider-agnostic:

```csharp
var apiKey = builder.Configuration["Payments:ApiKey"];
var connection = builder.Configuration.GetConnectionString("Primary");

builder.Services
    .AddOptions<PaymentsOptions>()
    .Bind(builder.Configuration.GetRequiredSection("Payments"))
    .ValidateOnStart();
```

The options pattern is the preferred consumer API for groups of related
settings. `IOptions<T>` is read once, while `IOptionsMonitor<T>` and
`IOptionsSnapshot<T>` can observe provider reload tokens according to their
normal singleton/scoped semantics.

### 4.2 Provider ordering

.NET resolves duplicate keys from the last-added provider. Adding Sable after
`WebApplication.CreateBuilder(args)` means Sable values override the default
JSON, Development User Secrets, environment, and command-line providers.
Applications that intentionally require deployment environment variables to
override Sable must add a prefixed environment provider after Sable.

Bootstrap database/key-file paths can be read from the configuration that
exists before Sable is added. They must not be read from Sable itself.

ASP.NET Core User Secrets is an unencrypted development JSON file, not a
trusted production store. It can supply local-development bootstrap paths but
does not replace the encrypted embedded store.

## 5. Storage model

Table: `sable_configuration_entry`

| Field | Protection | Purpose |
|---|---|---|
| `id` | Clear | SHA-256 digest of the ordinal-case-insensitive key |
| `key` | Clear | Configuration hierarchy and snapshot enumeration |
| `value` | Encrypted envelope | Configuration value |
| `updated_at` | Clear | Operational refresh/change metadata |

Configuration keys are clear by design. Encrypting them would prevent the
provider from enumerating the hierarchy and detecting case-insensitive
duplicates.

The internal record ID is a lowercase SHA-256 hex digest of the normalized key.
This is an intentional exception to the normal Sable `long` ID convention:
configuration writes require deterministic, race-free, case-insensitive
upserts, while a 64-bit derived ID would introduce avoidable collision risk.

The key, database namespace, database name, fixed table name, record digest,
storage field, and codec are authenticated as AEAD associated data. Moving an
envelope to another key record, database, or field therefore fails
authentication.

## 6. Cryptography and bootstrap

- Payload default: `EncryptionAlgorithm.Aes256Gcm`.
- Alternate payload: `EncryptionAlgorithm.ChaCha20Poly1305`.
- One fresh 256-bit DEK and nonce per value write.
- The DEK is wrapped through the configured `IKeyWrappingProvider`.
- The default bootstrap helper loads a raw or Base64-encoded 32-byte KEK from a
  mounted file.
- The database path, mounted-key path, key ID, namespace, and database name
  must come from explicit host/bootstrap configuration, environment variables,
  or the deployment platform. They cannot come from this store itself.
- The package never silently generates or replaces a KEK. Losing the KEK makes
  existing values unrecoverable.

The first implementation reuses the Sable field-encryption envelope kernel
instead of inventing a configuration-specific cipher format.

## 7. Runtime and lifecycle

The .NET configuration contract is synchronous at startup. The provider
therefore performs one bounded blocking load while `ConfigurationBuilder.Build`
runs, matching the standard database-provider pattern documented by Microsoft.
All direct storage and refresh operations remain asynchronous.

One reference-counted SurrealKV client is held per normalized database path in
the process and shared by configuration providers and direct CRUD stores.
Operations are serialized because the embedded client owns mutable session
state. The client is disposed synchronously when its last store/provider owner
is disposed. Cross-process concurrent ownership of the same embedded store is
unsupported in V1.

`RefreshAsync` loads and decrypts a new snapshot, compares it with the current
case-insensitive snapshot, swaps the snapshot atomically, and calls
`OnReload()` only when a value was added, changed, or removed.

## 8. Failure semantics

- Missing database path or key provider: configuration error before database I/O.
- Missing mounted-key file: startup fails; no replacement key is generated.
- Wrong key or modified AAD/envelope: Sable authentication exception.
- Duplicate logical keys differing only by case: fail closed.
- Oversized key/value: reject before encryption or persistence.
- `IConfiguration` write attempt: clear read-only-provider exception.
- Empty store: valid empty configuration snapshot.

Exception messages identify the configuration key only when doing so is useful
for diagnosis. They never include plaintext values, keys, ciphertext, or KEK
material.

## 9. Testing requirements

- Round-trip CRUD through persistent SurrealKV.
- Persistence after closing and reopening the database.
- Raw stored records do not contain plaintext values.
- Case-insensitive key upsert and lookup.
- Configuration hierarchy and provider precedence.
- Explicit refresh and change-token behavior.
- Delete and missing-key behavior.
- Wrong-key and tampered-context failures.
- Invalid key, database path, algorithm, and maximum-size validation.
- Read-only `IConfiguration` write failure.
- Targeted package tests plus the full `src/AeroDB.slnx` test run.

## 10. Relationship to prior documents

[sable-vault-secrets-store.md](sable-vault-secrets-store.md) remains historical
brainstorming. Its `AddSableVault()` name, remote credentials, self-provisioning,
multi-tenancy, distributed locking, and Vault security claims do not apply to
this package.

[sable-data-encryption-and-vault-architecture.md](sable-data-encryption-and-vault-architecture.md)
remains authoritative for field encryption and the future dedicated Vault
service. This document is authoritative for the embedded configuration-store
package.

## 11. References

- [Implement a custom configuration provider in .NET](https://learn.microsoft.com/dotnet/core/extensions/custom-configuration-provider)
- [SurrealDB .NET SDK](https://surrealdb.com/docs/sdk/dotnet)
- [SurrealDB security best practices](https://surrealdb.com/docs/learn/security/best-practices/security-best-practices)
