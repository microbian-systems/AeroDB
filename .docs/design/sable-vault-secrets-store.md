# AeroDB.Sable.Vault — Encrypted Configuration Provider & Secrets Store

> **Status:** Historical brainstorming only — not an implementation specification.
> **Do not implement directly from this document.** It preserves early ideas, alternatives,
> and unresolved assumptions for context. The current proposed direction is
> [AeroDB.Sable Data Encryption and Vault Architecture](sable-data-encryption-and-vault-architecture.md).
> **Last updated:** 2026-07-18
> **Decision log:** See [§ Decision Registry](#decision-registry) for confirmed vs tentative choices.

---

## Table of Contents

1. [Vision](#vision)
2. [What We Need to Solve](#what-we-need-to-solve)
3. [Decision Registry](#decision-registry)
3. [Configuration Provider Architecture](#configuration-provider-architecture)
4. [Connecting to the Vault — Credential Chain](#connecting-to-the-vault--credential-chain)
5. [Encryption Architecture](#encryption-architecture)
6. [Bootstrap & Provisioning Flow](#bootstrap--provisioning-flow)
7. [SurrealDB Schema Design](#surrealdb-schema-design)
8. [Authentication Model](#authentication-model)
9. [Multi-Tenancy](#multi-tenancy)
10. [Distributed Locking](#distributed-locking)
11. [API Surface](#api-surface)
12. [Project Structure](#project-structure)
13. [Open Questions & TBD](#open-questions--tbd)
14. [Implementation Phases](#implementation-phases)
15. [References](#references)

---

## Vision

A distributed secrets store (analogous to Azure Key Vault, HashiCorp Vault, or Infisical) backed by **SurrealDB**, using **AES-256-GCM envelope encryption** for data-at-rest security. The provider plugs into ASP.NET Core's `IConfiguration` pipeline so any .NET application can consume encrypted secrets transparently — no code changes needed beyond a single `AddSableVault()` registration.

**Core principles:**

- **Self-provisioning.** On first startup, the provider bootstraps from a local encrypted store (RocksDB/SurrealKV + DPAPI), connects to a SurrealDB server, and auto-creates all required schemas, access definitions, and tenant key material. Bootstrap configuration is then migrated into the vault. Subsequent starts detect the local store and connect immediately.
- **Multi-tenant by design.** Secrets are isolated per tenant via SurrealDB's namespace/database hierarchy and row-level `PERMISSIONS`. Suitable for SaaS platforms hosting many customers.
- **M2M + human auth.** Service accounts (`BEARER`) for machine-to-machine secret consumption; record users (`RECORD` access) for human operators; JWT federation for existing IdP integration.
- **Envelope encryption.** Three-tier key hierarchy (Master → Tenant → Per-Secret). Only wrapped keys and ciphertext are stored in SurrealDB. The master key is managed by a pluggable `IKeyManagementService` (DPAPI default, Azure Key Vault / AWS KMS as future providers).
- **Live reload.** Secrets can be rotated and the configuration provider publishes change notifications via `IChangeToken`, so apps pick up new values without restarting.

---

## What We Need to Solve

Storing encrypted connection strings and secrets directly in a database is a classic architectural conundrum. It seems perfectly secure on paper: the database is encrypted at rest (e.g., using LUKS, BitLocker, or cloud provider storage encryption), so if someone steals the physical hard drives or backups, the data is unreadable.

However, relying solely on **encryption at rest** to protect secrets inside the database introduces a critical architectural flaw known as the **Confused Deputy Problem**.

This is why relying on database encryption at rest alone falls short for secrets management, and why specialized secret stores are required. AeroDB.Sable.Vault must address each of these head-on.

---

### 1. The Decryption Lifecycle (The "Always-On" Vulnerability)

Encryption at rest only protects data when the database engine is stopped and the storage volume is unmounted.

- As soon as the database server boots up and mounts the encrypted disk, the operating system or datastore backend supplies the decryption keys.
- From that moment on, the database engine reads and writes decrypted data transparently.

If an attacker gains unauthorized access to your running database session (via SQL Injection, compromised service account credentials, or a remote code execution vulnerability), the database will happily decrypt and serve those secrets right to them. The database acts as a **confused deputy** — using its legitimate permissions to fetch data on behalf of an illegitimate actor.

**How Sable Vault addresses this:**

- **Application-layer envelope encryption.** Secrets are encrypted *before* they touch SurrealDB. The ciphertext stored in `vault_secrets` is useless without the DEK, which is itself wrapped by a tenant intermediate key that SurrealDB never sees unwrapped.
- **SurrealDB never holds plaintext keys.** The master key (KEK) lives outside SurrealDB entirely — in DPAPI, Azure Key Vault, or AWS KMS. Intermediate keys are stored *wrapped* in `vault_tenant_keys`. Even with full database access, an attacker gets only wrapped blobs.
- **Per-secret encryption keys (DEKs).** Even if one secret's DEK is somehow compromised, other secrets remain protected because each has its own independent key.

---

### 2. Circular Dependencies (The Chicken-and-Egg Problem)

To boot your application, it needs to connect to the database to fetch its configuration and third-party API secrets. But to connect to the database, it *already needs* the database connection string.

If you store that initial bootstrap connection string in plaintext on the application server config file to bypass this problem, you have simply moved the target rather than solving it.

**How Sable Vault addresses this:**

- **Layered bootstrap with local encrypted store.** The bootstrap connection configuration is never stored in plaintext. On first provision, the admin supplies credentials once (via environment variables or an interactive prompt). Sable Vault provisions the SurrealDB schemas, then writes an *encrypted* bootstrap file to a local SurrealKv store protected by DPAPI.
- **Subsequent starts use the encrypted bootstrap store.** The application server's `appsettings.json` never needs to contain SurrealDB credentials. Only the encrypted `./vault-bootstrap/bootstrap.dat` file holds the cached bearer token — and only the DPAPI-scoped machine identity can decrypt it.
- **The `DefaultSableCredential` chain breaks the circle.** The credential chain tries: bearer token from env → local encrypted store → appsettings fallback → interactive prompt. Each step is a progressively less secure fallback, but none stores plaintext credentials in source-controlled configuration files.

---

### 3. Lack of True Secret Separation of Concerns

Databases are designed for high-throughput data retrieval, indexing, complex relationships, and querying. They are not built with the strict constraints required for operational security:

- **The Master Key Exposure:** If your application handles its own encryption/decryption before sending data to the database (field-level encryption), the application must hold the master encryption key in memory. If the app server is compromised, the key is gone.
- **Audit Trails:** Standard database transaction logs track *data modifications* (`INSERT`/`UPDATE`), but rarely log explicit, immutable audit records for every single read (`SELECT`) of a specific field. If a secret is leaked via a read operation, you may never know.

**How Sable Vault addresses this:**

- **Master key never lives in application memory.** The KEK is resolved by the pluggable `IKeyManagementService` only when needed for key wrapping/unwrapping operations, and zeroed immediately after use. DEKs are generated per-operation, used, and zeroed. The application's normal request-processing memory space never holds key material.
- **Immutable audit trail in `vault_audit`.** Every secret access (read, write, delete, rotate) writes an append-only audit record to the `vault_audit` table. Permissions on this table prevent modification or deletion. This gives non-repudiation: you know exactly who accessed what secret and when.
- **Strict identity-based access via SurrealDB PERMISSIONS.** Table-level and field-level `PERMISSIONS` enforce that even a compromised database session can only access secrets scoped to its authenticated tenant — row-level security enforced by the database engine itself, not application code.
- **Per-tenant key isolation.** Compromising one tenant's intermediate key exposes only that tenant's secrets. Other tenants remain protected. This limits blast radius in a multi-tenant deployment.

---

### The Blueprint: What Makes a Dedicated Secret Store

To secure connection strings and application secrets, the industry standard is to use a dedicated tool like **HashiCorp Vault**, **AWS Secrets Manager**, **Azure Key Vault**, or **Google Cloud Secret Manager**. AeroDB.Sable.Vault aims to provide equivalent guarantees on SurrealDB infrastructure.

| Capability | Why It Matters | Sable Vault Approach |
|------------|---------------|---------------------|
| **Memory-only / sealed state** | No plaintext on disk; keys only in memory when actively used | DEKs generated per-operation, zeroed after use; KEK in external KMS |
| **Granular identity-based access (IAM)** | App authenticates with machine identity; only requests secrets it owns | SurrealDB BEARER tokens with tenant-scoped `PERMISSIONS` |
| **Strict read auditing** | Non-repudiation audit log for every secret access | `vault_audit` table: append-only, immutable, tenant-scoped |
| **Dynamic secret generation** | Auto-rotate credentials, making stolen credentials short-lived | `SecretRotator` background daemon; rotation policies per secret |
| **No circular dependencies** | Bootstrap without plaintext config files | Layered `DefaultSableCredential` chain + local encrypted bootstrap store |
| **Application-layer encryption** | Ciphertext at rest in database; keys external to database | AES-256-GCM envelope encryption; KEK never in SurrealDB |

---

> **Key takeaway:** Encryption at rest is a checkbox for physical compliance — it is not an active firewall against dynamic application-layer breaches. AeroDB.Sable.Vault must provide defense-in-depth: envelope encryption with external key management, per-tenant key isolation, immutable audit logging, and a credential chain that eliminates plaintext bootstrap configuration.

---

## Decision Registry

| # | Decision | Status | Rationale |
|---|----------|--------|-----------|
| D1 | Project name: `AeroDB.Sable.Vault` | ✅ Confirmed | Distinct from core `AeroDB.Sable` library; "Vault" communicates purpose. |
| D2 | Bootstrap: layered (local DB → appsettings.json) | ✅ Confirmed | Local encrypted store for fast restart; appsettings.json/env as fallback for first provision. |
| D3 | Auth: both M2M + human | ✅ Confirmed | Service accounts for apps, record users for operators. |
| D4 | Master key: pluggable `IKMS` interface | ✅ Confirmed | DPAPI default, cloud KMS as future providers. |
| D5 | KEK sharing via SurrealDB key ring | ⚠️ Tentative | Needs threat model analysis. Alternative: each provisioning machine generates its own KEK and shares wrapped intermediate keys only. |
| D6 | Encryption algorithm: AES-256-GCM | ✅ Confirmed | Industry gold standard for authenticated encryption. |
| D7 | Key hierarchy: 3-tier (KEK → IK → DEK) | ✅ Confirmed | Follows GoDaddy Asherah / AWS KMS envelope pattern. |
| D8 | Distributed locking: SurrealDB conditional UPDATE | ⚠️ Tentative | FusionCache-style pattern; needs SurrealDB concurrency semantics verification. |
| D9 | Human UI: deferred to Phase 6 (Blazor dashboard) | ✅ Confirmed | Out of scope for initial delivery. |
| D10 | Configuration provider pattern: `ConfigurationProvider` base class | ✅ Confirmed | Standard ASP.NET Core pattern as used by Azure Key Vault provider. |

---

## Configuration Provider Architecture

`AeroDB.Sable.Vault` follows the standard ASP.NET Core configuration provider pattern, identical to how the Azure Key Vault provider works.

### Class Model

```
IConfigurationBuilder
  └── .AddSableVault(options)           Extension method
        └── SableVaultConfigurationSource : IConfigurationSource
              └── .Build(builder) → returns SableVaultConfigurationProvider
                    └── SableVaultConfigurationProvider : ConfigurationProvider
                          ├── Load()              Fetch all secrets from SurrealDB into Data dictionary
                          ├── TryGet(key, out)    Return from in-memory Data (fast path)
                          ├── Set(key, value)     Push secret update to SurrealDB
                          └── GetReloadToken()    IChangeToken for live refresh notification
```

### Configuration Source Registration

The provider is registered **after** `appsettings.json` and environment variables so it has **higher priority** — vault secrets override local config:

```csharp
// Program.cs — Minimal API (ASP.NET Core 6+)
var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddSableVault(options =>
{
    // Bootstrap connection
    options.Bootstrap = new BootstrapOptions
    {
        LocalStore = new SurrealKvOptions { Path = "./vault-bootstrap" },
        Endpoint = "ws://surrealdb.internal:8000",
        Namespace = "vault",
        Database = "secrets",
        Credentials = Credentials.FromEnvironment()
    };

    // Encryption
    options.Encryption.KeyManagement = new DpapiKeyManagement();

    // Tenancy
    options.Tenancy.Mode = TenantResolutionMode.ClaimBased;
    options.Tenancy.ClaimType = "tenant_id";
});

// Or: generic host (Worker Service, Console)
Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, config) =>
    {
        config.AddSableVault(options => { /* ... */ });
    });
```

### How TryGet Works (Per-Request Resolution)

Every time `IConfiguration["SomeKey"]` is called, the configuration root iterates all providers in priority order. `SableVaultConfigurationProvider.TryGet()` checks its in-memory `Data` dictionary (populated by `Load()`). This is O(1) and does **not** hit SurrealDB on every read.

### Live Reload via IChangeToken

The `GetReloadToken()` method returns an `IChangeToken`. When a secret is rotated (via admin API, background rotation daemon, or manual update), the provider calls `OnReload()` which:
1. Invalidates the previous change token
2. Re-executes `Load()` to fetch fresh data from SurrealDB
3. Creates a new change token
4. Downstream consumers (e.g., options pattern with `IOptionsSnapshot<T>`) pick up new values

```csharp
// Internally
public void NotifySecretChanged(string key)
{
    _logger.LogDebug("Secret rotated: {Key}", key);
    Load();  // Reload all secrets from SurrealDB
    OnReload();  // Signal IChangeToken
}
```

### Configuration Key Convention

Secrets are stored with hierarchical keys using colon (`:`) separators, consistent with the ASP.NET Core configuration system:

```
api:stripe:secret_key        → sk_live_xxx
db:postgres:connection       → Host=db;Database=app;...
email:smtp:password          → smtp_pass_xxx
tenant:acme:api:openai:key   → sk-xxx
```

The provider flattens this hierarchy into the `Data` dictionary on `Load()`, and `IConfiguration` resolves sections naturally via `GetSection("api:stripe")`.

### Non-ASP.NET Usage

For console apps, workers, or any non-web .NET app, the vault can be used directly without the configuration pipeline:

```csharp
// Direct access
var vault = await SableVault.CreateAsync(options);
await vault.SetSecretAsync("api:stripe:key", "sk_live_xxx");

// Read
var stripeKey = await vault.GetSecretAsync("api:stripe:key");

// Tenant-scoped access
var tenantVault = vault.ForTenant("acme");
await tenantVault.SetSecretAsync("api:openai:key", "sk-xxx");
```

---

## Connecting to the Vault — Credential Chain

This section describes how consuming applications connect and authenticate to a Sable Vault instance. The design is modeled directly on **Azure Key Vault's `AddAzureKeyVault`** connection pattern — specifically the `DefaultAzureCredential` credential chain, `KeyVaultSecretManager` for key mapping, and `AzureKeyVaultConfigurationOptions` for reload behavior.

### Reference: How Azure Key Vault Does It

The canonical Azure Key Vault registration requires **only a vault name** — authentication is fully automatic via `DefaultAzureCredential`:

```csharp
// Canonical Key Vault pattern — minimal configuration
builder.Configuration.AddAzureKeyVault(
    new Uri($"https://{builder.Configuration["KeyVaultName"]}.vault.azure.net/"),
    new DefaultAzureCredential());
```

`DefaultAzureCredential` tries credential sources **in priority order**, stopping at the first success:

1. `AZURE_CLIENT_ID` + `AZURE_CLIENT_SECRET` environment variables
2. Workload Identity (Kubernetes)
3. Managed Identity (Azure hosting — system-assigned or user-assigned)
4. Visual Studio / VS Code signed-in developer account
5. Azure CLI `az login`
6. Interactive browser (developer fallback)

**Key insight:** The same code works in local development (VS/Azure CLI), CI/CD (environment variables), and production (managed identity) with **zero code changes and zero credentials in configuration files**.

### Sable Vault Equivalent: `DefaultSableCredential`

We mirror this with a `DefaultSableCredential` chain that resolves SurrealDB connection credentials automatically:

```
DefaultSableCredential tries in priority order:
  ┌─ 1. SABLE_VAULT_BEARER_TOKEN               env var (production, CI/CD)
  ├─ 2. SABLE_VAULT_USER + SABLE_VAULT_PASS     env vars (service account)
  ├─ 3. Local bootstrap store                    SurrealKv file (dev machines, survives rebuilds)
  ├─ 4. appsettings.json bootstrap               fallback (first-time provision only)
  └─ 5. Interactive prompt                       console input (dev fallback, opt-in)
```

#### 1. Bearer Token (Production / CI/CD)

```bash
# Production — SurrealDB BEARER access token (M2M)
export SABLE_VAULT_ENDPOINT="ws://surrealdb.prod.internal:8000"
export SABLE_VAULT_BEARER_TOKEN="bearer:vault_app_payments:abc123..."

# CI/CD — generated per pipeline run
export SABLE_VAULT_ENDPOINT="ws://surrealdb.ci:8000"
export SABLE_VAULT_BEARER_TOKEN="bearer:vault_ci_deploy:def456..."
```

Highest priority. No configuration file changes needed. Equivalent to Azure's managed identity.

#### 2. User/Password (Service Accounts)

```bash
export SABLE_VAULT_ENDPOINT="ws://surrealdb.staging:8000"
export SABLE_VAULT_USER="svc_vault_reader"
export SABLE_VAULT_PASS="..."
```

For environments without bearer token support. Falls through to this if `SABLE_VAULT_BEARER_TOKEN` is not set.

#### 3. Local Bootstrap Store (Developer Machines)

After first-time provisioning, the vault writes an encrypted bootstrap file to `./vault-bootstrap/bootstrap.dat` (SurrealKv + DPAPI). On subsequent starts, this file contains the cached endpoint + bearer token — no env vars or config files needed. Equivalent to Azure's `VisualStudioCredential` / `AzureCliCredential` (developer convenience).

#### 4. appsettings.json (First-Time Provision)

```json
{
  "SableVault": {
    "Endpoint": "ws://surrealdb:8000",
    "Namespace": "vault",
    "Database": "secrets",
    "Username": "root",
    "Password": "root"
  }
}
```

Only used on very first startup when no local bootstrap store exists. After provisioning, the bootstrap store is written and this config is no longer consulted.

#### 5. Interactive Prompt (Developer Fallback)

If no other credential source succeeds and `SABLE_VAULT_INTERACTIVE=true` is set, prompts the developer on the console:

```
[Sable Vault] No credential found. Interactive mode enabled.
Endpoint (ws://localhost:8000): 
Username: root
Password: ********
```

Opt-in only — must set `SABLE_VAULT_INTERACTIVE=true` to enable.

### `DefaultSableCredential` Implementation

```csharp
/// <summary>
/// Mirror of Azure.Identity.DefaultAzureCredential.
/// Tries credential sources in priority order, stops at first success.
/// Works from local dev → CI/CD → production with zero code changes.
/// </summary>
public class DefaultSableCredential : ISableCredential
{
    private readonly List<ISableCredentialSource> _sources = new()
    {
        new EnvironmentBearerCredential("SABLE_VAULT_BEARER_TOKEN"),
        new EnvironmentUserPasswordCredential("SABLE_VAULT_USER", "SABLE_VAULT_PASS"),
        new LocalBootstrapCredential("./vault-bootstrap"),
        new AppSettingsBootstrapCredential(),
        new InteractiveCredential()
    };

    public async Task<VaultCredentials> GetCredentialAsync(CancellationToken ct = default)
    {
        foreach (var source in _sources)
        {
            try
            {
                var cred = await source.TryGetCredentialAsync(ct);
                if (cred is not null)
                    return cred;
            }
            catch (Exception ex) when (ex is not VaultAuthenticationException)
            {
                // Log and continue to next source
                _logger.LogDebug(ex, "Credential source {Source} failed, trying next", source.GetType().Name);
            }
        }

        throw new VaultAuthenticationException(
            "No Sable Vault credential available. Set SABLE_VAULT_BEARER_TOKEN, " +
            "SABLE_VAULT_USER/SABLE_VAULT_PASS, or ensure a local bootstrap store exists. " +
            "For first-time setup, configure SableVault in appsettings.json.");
    }
}

public interface ISableCredential
{
    Task<VaultCredentials> GetCredentialAsync(CancellationToken ct = default);
}
```

### Default Configuration — No Arguments Required

Following the Key Vault pattern, the simplest registration requires **no arguments at all** when environment variables are set:

```csharp
// Production / CI / dev — auto-resolves via DefaultSableCredential
builder.Configuration.AddSableVault();

// Explicit URI + credential (like Key Vault URI pattern)
builder.Configuration.AddSableVault(
    new Uri("ws://surrealdb.internal:8000"),
    new DefaultSableCredential());

// Full configuration with options (like AzureKeyVaultConfigurationOptions)
builder.Configuration.AddSableVault(options =>
{
    options.Endpoint = "ws://surrealdb.internal:8000";
    options.Namespace = "vault";
    options.Database = "secrets";
    options.Credential = new DefaultSableCredential();

    // Equivalent to AzureKeyVaultConfigurationOptions.ReloadInterval
    options.ReloadInterval = TimeSpan.FromMinutes(5);

    // Equivalent to KeyVaultSecretManager
    options.SecretManager = new PrefixSableVaultSecretManager("v1");
});

// Pre-configured SurrealDB client injection (like SecretClient injection)
var surrealClient = new SurrealDbClient("ws://surrealdb.internal:8000");
builder.Configuration.AddSableVault(surrealClient, new DefaultSableCredential());
```

### `SableVaultSecretManager` — Controlling Secret Loading

Azure Key Vault uses `KeyVaultSecretManager` to control **which** secrets are loaded and **how** their names are mapped to configuration keys. We mirror this with `SableVaultSecretManager`:

```csharp
/// <summary>
/// Controls how vault secrets are mapped to IConfiguration keys.
/// Equivalent to Azure's KeyVaultSecretManager.
/// </summary>
public class SableVaultSecretManager
{
    /// <summary>Whether to load this secret into configuration. Default: all secrets.</summary>
    public virtual bool Load(SecretRecord secret) => true;

    /// <summary>Map secret key to configuration key. Default: identity mapping.</summary>
    public virtual string GetKey(SecretRecord secret) => secret.Key;
}
```

#### Built-in Implementations

```csharp
/// <summary>
/// Only loads secrets whose key starts with a given prefix.
/// Strips the prefix when mapping to configuration.
/// Equivalent to Key Vault's version-prefix pattern.
/// </summary>
public class PrefixSableVaultSecretManager : SableVaultSecretManager
{
    private readonly string _prefix;

    public PrefixSableVaultSecretManager(string prefix)
        => _prefix = $"{prefix}-";

    public override bool Load(SecretRecord secret)
        => secret.Key.StartsWith(_prefix);

    public override string GetKey(SecretRecord secret)
        => secret.Key[_prefix.Length..];
}

// Usage: secrets stored as "v1-api:stripe:key" → loaded as "api:stripe:key"
builder.Configuration.AddSableVault(options =>
{
    options.SecretManager = new PrefixSableVaultSecretManager("v1");
});

/// <summary>
/// Excludes expired secrets from configuration.
/// Equivalent to Key Vault's SampleKeyVaultSecretManager.
/// </summary>
public class ExcludeExpiredSecretManager : SableVaultSecretManager
{
    public override bool Load(SecretRecord secret)
        => secret.ExpiresAt is null || secret.ExpiresAt > DateTimeOffset.UtcNow;
}
```

### Environment Variable Conventions

| Variable | Purpose | Priority |
|----------|---------|----------|
| `SABLE_VAULT_ENDPOINT` | SurrealDB endpoint (ws:// or https://) | Required for non-bootstrap connections |
| `SABLE_VAULT_BEARER_TOKEN` | SurrealDB BEARER access token | 1 (highest) |
| `SABLE_VAULT_USER` | SurrealDB username (with `_PASS`) | 2 |
| `SABLE_VAULT_PASS` | SurrealDB password (with `_USER`) | 2 |
| `SABLE_VAULT_NAMESPACE` | SurrealDB namespace (default: `vault`) | Optional |
| `SABLE_VAULT_DATABASE` | SurrealDB database (default: `secrets`) | Optional |
| `SABLE_VAULT_TENANT_ID` | Tenant ID for secret scoping | Optional |
| `SABLE_VAULT_INTERACTIVE` | Enable interactive console prompt (dev only) | Optional (`true`/`false`) |

### Connection-to-Provider Mapping (Summary)

| Azure Key Vault | Sable Vault Equivalent | Notes |
|-----------------|------------------------|-------|
| `KeyVaultName` (string in appsettings) | `SABLE_VAULT_ENDPOINT` env var | Direct URI, no DNS convention |
| Vault URI: `https://{name}.vault.azure.net/` | `ws://{host}:{port}` or `https://{host}:{port}` | SurrealDB endpoint |
| `DefaultAzureCredential` (auto chain) | `DefaultSableCredential` (auto chain) | Same chain-of-responsibility pattern |
| `ManagedIdentityCredential` (production) | `EnvironmentBearerCredential` (production) | SurrealDB BEARER access token |
| `ClientCertificateCredential` (certificate) | `UserPasswordCredential` | SurrealDB root/user credentials |
| `SecretClient` (SDK client injection) | `ISurrealDbClient` injection | Custom client sharing |
| `KeyVaultSecretManager.Load()` | `SableVaultSecretManager.Load()` | Secret filtering |
| `KeyVaultSecretManager.GetKey()` | `SableVaultSecretManager.GetKey()` | Key name mapping |
| `AzureKeyVaultConfigurationOptions.ReloadInterval` | `VaultOptions.ReloadInterval` | Periodic secret refresh |
| Secret name: alphanumeric + `-` only | Secret key: any string (colons OK) | SurrealDB allows `:` in field values |
| Key delimiter transform: `--` → `:` | Native colons — no transform needed | SurrealDB key names support `:` |

---

## Encryption Architecture

### Design Goals

- **Data-at-rest encryption.** Even if an attacker gains full access to the SurrealDB server, secrets remain unreadable without the master key.
- **Per-tenant key isolation.** Compromising one tenant's intermediate key does not expose other tenants.
- **Per-secret encryption keys.** Each secret gets its own DEK, limiting blast radius.
- **Authenticated encryption.** AES-256-GCM provides confidentiality + integrity + authenticity in a single operation.
- **Crypto agility.** The pluggable `IKeyManagementService` allows swapping KEK providers without changing the core encryption logic.

### Key Hierarchy (3-Tier Envelope Encryption)

This follows the **GoDaddy Asherah** and **AWS KMS** envelope encryption pattern:

```
┌──────────────────────────────────────────────────┐
│ Master Key (KEK — Key Encryption Key)            │
│  Managed by IKeyManagementService                │
│  DPAPI (local dev) / Azure Key Vault / AWS KMS   │
│  Long-lived (rotated on schedule)                │
│  NEVER stored in SurrealDB                       │
└───────────────┬──────────────────────────────────┘
                │ wraps
┌───────────────▼──────────────────────────────────┐
│ Tenant Root Key (IK — Intermediate Key)          │
│  One per tenant                                  │
│  Stored in SurrealDB: vault_tenant_keys          │
│  Stored as: { key_id, wrapped_ik, algorithm }    │
│  Medium-lived (rotated per policy)               │
└───────────────┬──────────────────────────────────┘
                │ wraps
┌───────────────▼──────────────────────────────────┐
│ Data Encryption Key (DEK)                        │
│  Generated per secret, per version               │
│  AES-256 key material (32 bytes)                 │
│  Stored alongside ciphertext: wrapped_dek field  │
│  Short-lived (re-generated on each update)       │
└───────────────┬──────────────────────────────────┘
                │ encrypts
┌───────────────▼──────────────────────────────────┐
│ Secret Payload (ciphertext)                      │
│  Stored in SurrealDB: vault_secrets              │
│  Format: { ciphertext, wrapped_dek, nonce, tag,  │
│            key_version, algorithm, created_at }  │
└──────────────────────────────────────────────────┘
```

### Encryption Operation (per secret write)

```csharp
public async Task<EncryptedPayload> EncryptAsync(string tenantId, byte[] plaintext)
{
    // 1. Resolve or create tenant intermediate key
    var tenantKey = await _keyStore.GetOrCreateTenantKeyAsync(tenantId);

    // 2. Generate DEK (AES-256)
    var dek = new byte[32];  // 256 bits
    RandomNumberGenerator.Fill(dek);

    // 3. Encrypt plaintext with DEK
    var nonce = new byte[12];  // 96 bits for GCM
    RandomNumberGenerator.Fill(nonce);
    var tag = new byte[16];    // 128-bit authentication tag

    var ciphertext = new byte[plaintext.Length];
    using var aes = new AesGcm(dek, tag.Length);
    aes.Encrypt(nonce, plaintext, ciphertext, tag);

    // 4. Wrap DEK with tenant IK
    var wrappedDek = await _keyManagement.WrapKeyAsync(tenantKey.UnwrappedIk, dek);

    // 5. Zero sensitive material
    CryptographicOperations.ZeroMemory(dek);

    return new EncryptedPayload
    {
        Ciphertext = ciphertext,
        WrappedDek = wrappedDek,
        Nonce = nonce,
        Tag = tag,
        KeyId = tenantKey.KeyId,
        KeyVersion = tenantKey.Version,
        Algorithm = "AES-256-GCM"
    };
}
```

### Decryption Operation (per secret read)

```csharp
public async Task<byte[]> DecryptAsync(string tenantId, EncryptedPayload payload)
{
    // 1. Resolve tenant intermediate key
    var tenantKey = await _keyStore.GetTenantKeyAsync(tenantId, payload.KeyId);

    // 2. Unwrap DEK using tenant IK
    var dek = await _keyManagement.UnwrapKeyAsync(tenantKey.UnwrappedIk, payload.WrappedDek);

    // 3. Decrypt ciphertext with DEK
    var plaintext = new byte[payload.Ciphertext.Length];
    using var aes = new AesGcm(dek, payload.Tag.Length);
    aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext);

    // 4. Zero sensitive material
    CryptographicOperations.ZeroMemory(dek);

    return plaintext;
}
```

### Key Management Interface (Pluggable)

```csharp
/// <summary>
/// Pluggable master key management. Implementations:
/// - DpapiKeyManagementService (default, local dev/single-machine)
/// - AzureKeyVaultKeyManagementService (cloud, production)
/// - AwsKmsKeyManagementService (cloud, production)
/// - SurrealDbKeyRingService (shared KEK, tentative — needs threat model review)
/// </summary>
public interface IKeyManagementService
{
    /// <summary>Wrap (encrypt) a DEK using the master key.</summary>
    Task<byte[]> WrapKeyAsync(byte[] unwrappedKey, byte[] keyToWrap);

    /// <summary>Unwrap (decrypt) a DEK using the master key.</summary>
    Task<byte[]> UnwrapKeyAsync(byte[] unwrappedKey, byte[] wrappedKey);

    /// <summary>Generate a new intermediate key for a tenant.</summary>
    Task<byte[]> GenerateIntermediateKeyAsync();

    /// <summary>Get or create the master key. Only called during provisioning.</summary>
    Task<byte[]> GetOrCreateMasterKeyAsync();

    /// <summary>Rotate the master key (re-wrap all intermediate keys).</summary>
    Task RotateMasterKeyAsync(CancellationToken ct);
}
```

### Memory Hygiene

- All key material is stored in `byte[]` (never `string` — strings are immutable and cannot be zeroed).
- `CryptographicOperations.ZeroMemory()` is called after every use.
- Keys are never logged or included in exception messages.
- `IDisposable` / `IAsyncDisposable` on encryption services to ensure cleanup.

---

## Bootstrap & Provisioning Flow

### Startup Sequence

```
App.Startup()
  │
  ├─ 1. Try local encrypted bootstrap store
  │     ├─ SurrealKv at ./vault-bootstrap/
  │     ├─ Read bootstrap.json → { endpoint, ns, db, credentials, tenant_config }
  │     ├─ Decrypt with DPAPI (machine-scoped)
  │     ├─ Found → Connect to SurrealDB → SKIP to step 5
  │     └─ Not found → continue
  │
  ├─ 2. Read appsettings.json / environment variables
  │     ├─ SableVault:Bootstrap:Endpoint
  │     ├─ SableVault:Bootstrap:Namespace
  │     ├─ SableVault:Bootstrap:Database
  │     ├─ SableVault:Bootstrap:Username (or env: SABLE_VAULT_USERNAME)
  │     └─ SableVault:Bootstrap:Password (or env: SABLE_VAULT_PASSWORD)
  │
  ├─ 3. Connect to SurrealDB for provisioning
  │     ├─ Authenticate as root/admin user
  │     └─ VaultProvisioner.ProvisionAsync()
  │
  ├─ 4. VaultProvisioner (idempotent, re-runnable)
  │     ├─ DEFINE NAMESPACE vault (IF NOT EXISTS)
  │     ├─ DEFINE DATABASE secrets (IF NOT EXISTS)
  │     ├─ DEFINE ACCESS vault_bearer ON DATABASE TYPE BEARER
  │     │     DURATION FOR TOKEN 24h
  │     ├─ DEFINE ACCESS vault_record ON DATABASE TYPE RECORD
  │     │     SIGNUP (...) SIGNIN (...) DURATION FOR TOKEN 1h
  │     ├─ DEFINE TABLE vault_tenants SCHEMAFULL
  │     │     PERMISSIONS FOR select WHERE $auth.role = 'admin'
  │     │     PERMISSIONS FOR create,update,delete NONE
  │     ├─ DEFINE TABLE vault_secrets SCHEMAFULL
  │     │     PERMISSIONS FOR select WHERE tenant_id = $auth.tenant_id
  │     │     PERMISSIONS FOR create,update WHERE tenant_id = $auth.tenant_id
  │     ├─ DEFINE TABLE vault_tenant_keys SCHEMAFULL
  │     │     PERMISSIONS NONE  (only vault service account reads)
  │     ├─ DEFINE TABLE vault_lock SCHEMAFULL
  │     │     PERMISSIONS FOR select,update WHERE true
  │     └─ DEFINE TABLE vault_audit SCHEMAFULL
  │           PERMISSIONS FOR select WHERE $auth.tenant_id = tenant_id
  │
  ├─ 5. Generate tenant root keys
  │     ├─ For each configured tenant:
  │     │   ├─ Generate IK (AES-256)
  │     │   ├─ Wrap IK with KEK
  │     │   ├─ INSERT INTO vault_tenant_keys { tenant_id, key_id, wrapped_ik, ... }
  │     │   └─ Zero plaintext IK
  │     └─ For default tenant (if no explicit tenants configured)
  │
  ├─ 6. Migrate bootstrap config → vault_secrets
  │     ├─ Read all appsettings.json secrets
  │     ├─ Encrypt each with envelope encryption
  │     └─ INSERT INTO vault_secrets for each
  │
  ├─ 7. Write local bootstrap store
  │     ├─ Serialize { endpoint, ns, db, bearer_token }
  │     ├─ Encrypt with DPAPI
  │     └─ Write to ./vault-bootstrap/bootstrap.dat
  │
  └─ 8. Register SableVaultConfigurationProvider → serve secrets
```

### Idempotency Guarantees

- All `DEFINE` statements use `IF NOT EXISTS` equivalents.
- Key generation is idempotent — if a tenant key exists, it is reused.
- Bootstrap migration skips secrets that already exist (based on name + version).
- `VaultProvisioner` can be re-run safely (e.g., after schema upgrade).

### Failure Modes

| Scenario | Behavior |
|----------|----------|
| SurrealDB unreachable on startup | Throw `VaultConnectionException` with clear message. App fails fast. |
| Local bootstrap store corrupted | Delete and fall through to step 2 (appsettings.json). |
| Provisioning fails mid-way | Roll back schema changes where possible; log error; throw. Re-run on next startup. |
| Tenant key generation fails | Log + throw. No partial key records left. |
| Master key lost | **Catastrophic — all secrets permanently inaccessible.** Mitigation: backup KEK, redundant KMS. |

---

## SurrealDB Schema Design

### `vault_tenants`

```
DEFINE TABLE vault_tenants SCHEMAFULL
  PERMISSIONS
    FOR select WHERE $auth.role = 'admin' OR $auth.tenant_id = id
    FOR create, update, delete WHERE $auth.role = 'admin';

DEFINE FIELD id          ON vault_tenants TYPE string;    -- "acme", "default"
DEFINE FIELD name        ON vault_tenants TYPE string;    -- Display name
DEFINE FIELD created_at  ON vault_tenants TYPE datetime DEFAULT time::now();
DEFINE FIELD is_active   ON vault_tenants TYPE bool DEFAULT true;
DEFINE FIELD metadata    ON vault_tenants TYPE object DEFAULT {};

DEFINE INDEX idx_tenant_id ON vault_tenants FIELDS id UNIQUE;
```

### `vault_secrets`

```
DEFINE TABLE vault_secrets SCHEMAFULL
  PERMISSIONS
    FOR select WHERE tenant_id = $auth.tenant_id
    FOR create, update WHERE tenant_id = $auth.tenant_id
    FOR delete WHERE tenant_id = $auth.tenant_id AND $auth.role = 'admin';

DEFINE FIELD id              ON vault_secrets TYPE string;        -- Composite: "{tenant}:{key}"
DEFINE FIELD tenant_id       ON vault_secrets TYPE string;        -- FK to vault_tenants
DEFINE FIELD key             ON vault_secrets TYPE string;        -- "api:stripe:secret_key"
DEFINE FIELD ciphertext      ON vault_secrets TYPE bytes;         -- AES-256-GCM ciphertext
DEFINE FIELD wrapped_dek     ON vault_secrets TYPE bytes;         -- DEK wrapped by tenant IK
DEFINE FIELD nonce           ON vault_secrets TYPE bytes;         -- 12-byte GCM nonce
DEFINE FIELD tag             ON vault_secrets TYPE bytes;         -- 16-byte GCM auth tag
DEFINE FIELD key_id          ON vault_secrets TYPE string;        -- FK to vault_tenant_keys
DEFINE FIELD key_version     ON vault_secrets TYPE int;           -- IK version used
DEFINE FIELD algorithm       ON vault_secrets TYPE string DEFAULT "AES-256-GCM";
DEFINE FIELD version         ON vault_secrets TYPE int DEFAULT 1;  -- Secret version
DEFINE FIELD created_at      ON vault_secrets TYPE datetime DEFAULT time::now();
DEFINE FIELD updated_at      ON vault_secrets TYPE datetime DEFAULT time::now();
DEFINE FIELD created_by      ON vault_secrets TYPE string;        -- Auth principal
DEFINE FIELD expires_at      ON vault_secrets TYPE option<datetime>;
DEFINE FIELD rotation_policy ON vault_secrets TYPE option<string>; -- "30d", "90d", null
DEFINE FIELD metadata        ON vault_secrets TYPE object DEFAULT {};

DEFINE INDEX idx_secret_key  ON vault_secrets FIELDS tenant_id, key UNIQUE;
DEFINE INDEX idx_secret_exp  ON vault_secrets FIELDS expires_at;
```

### `vault_tenant_keys`

```
DEFINE TABLE vault_tenant_keys SCHEMAFULL
  PERMISSIONS NONE;  -- Only system-level access (vault service account)

DEFINE FIELD id              ON vault_tenant_keys TYPE string;    -- key_id
DEFINE FIELD tenant_id       ON vault_tenant_keys TYPE string;
DEFINE FIELD wrapped_ik      ON vault_tenant_keys TYPE bytes;    -- IK wrapped by KEK
DEFINE FIELD algorithm       ON vault_tenant_keys TYPE string DEFAULT "AES-256-GCM";
DEFINE FIELD key_version     ON vault_tenant_keys TYPE int DEFAULT 1;
DEFINE FIELD is_active       ON vault_tenant_keys TYPE bool DEFAULT true;
DEFINE FIELD created_at      ON vault_tenant_keys TYPE datetime DEFAULT time::now();
DEFINE FIELD rotated_at      ON vault_tenant_keys TYPE option<datetime>;

DEFINE INDEX idx_keys_tenant ON vault_tenant_keys FIELDS tenant_id, key_version;
```

### `vault_lock`

```
DEFINE TABLE vault_lock SCHEMAFULL
  PERMISSIONS
    FOR select, update WHERE true;   -- Any authenticated principal

DEFINE FIELD name           ON vault_lock TYPE string;           -- Lock name
DEFINE FIELD owner          ON vault_lock TYPE string;           -- Lock holder ID
DEFINE FIELD acquired_at    ON vault_lock TYPE datetime;         -- Acquisition timestamp
DEFINE FIELD expires_at     ON vault_lock TYPE datetime;         -- Lock timeout
DEFINE FIELD metadata       ON vault_lock TYPE object DEFAULT {};

DEFINE INDEX idx_lock_name  ON vault_lock FIELDS name UNIQUE;
```

Lock acquisition pattern:
```surql
-- Try to acquire lock (atomic conditional update)
LET $locked = (
    UPDATE ONLY vault_lock
    SET acquired_at = time::now(), owner = $owner, expires_at = time::now() + $timeout
    WHERE name = $lock_name
      AND (acquired_at IS NONE OR expires_at < time::now())
    RETURN AFTER
);

-- If $locked IS NOT NULL → lock acquired
-- If $locked IS NULL → lock held by another owner
```

### `vault_audit`

```
DEFINE TABLE vault_audit SCHEMAFULL
  PERMISSIONS
    FOR select WHERE tenant_id = $auth.tenant_id
    FOR create WHERE true  -- Any authenticated principal can write audit entries
    FOR update, delete NONE;

DEFINE FIELD id          ON vault_audit TYPE string;             -- Auto-generated
DEFINE FIELD tenant_id   ON vault_audit TYPE string;
DEFINE FIELD action      ON vault_audit TYPE string;             -- "READ", "WRITE", "DELETE", "ROTATE"
DEFINE FIELD secret_key  ON vault_audit TYPE string;             -- Which secret was accessed
DEFINE FIELD principal   ON vault_audit TYPE string;             -- Who accessed it
DEFINE FIELD ip_address  ON vault_audit TYPE option<string>;
DEFINE FIELD user_agent  ON vault_audit TYPE option<string>;
DEFINE FIELD timestamp   ON vault_audit TYPE datetime DEFAULT time::now();
DEFINE FIELD metadata    ON vault_audit TYPE object DEFAULT {};

DEFINE INDEX idx_audit_tenant ON vault_audit FIELDS tenant_id, timestamp;
DEFINE INDEX idx_audit_secret ON vault_audit FIELDS secret_key, timestamp;
```

---

## Authentication Model

### Auth Flow Matrix

| Actor | SurrealDB Mechanism | Token Type | Use Case |
|-------|-------------------|------------|---------|
| Vault service itself | `DEFINE USER ON DATABASE ... ROOT/OWNER` | Root credentials | Provisioning, key management, schema ops |
| .NET apps (M2M) | `DEFINE ACCESS TYPE BEARER` | Bearer token | `AddSableVault()` consumer apps |
| Human operators | `DEFINE ACCESS TYPE RECORD` | JWT (SurrealDB session) | Admin dashboard, manual secret management |
| Existing IdP | `DEFINE ACCESS TYPE JWT` + JWKS | External JWT | Azure AD, Auth0, Keycloak integration |

### Service Account Setup (BEARER)

```surql
-- Created during provisioning
DEFINE ACCESS vault_bearer ON DATABASE TYPE BEARER
  DURATION FOR TOKEN 24h;

-- Generate a bearer token for an app
CREATE bearer:app_payments;
```

The app stores the generated bearer token as an environment variable or in CI/CD secrets. The token is passed to `AddSableVault()`:

```csharp
builder.Configuration.AddSableVault(options =>
{
    options.Credentials = Credentials.FromBearer("bearer:app_payments:xxxx");
});
```

### Record User Setup (Human Operators)

```surql
-- Created during provisioning
DEFINE ACCESS vault_human ON DATABASE TYPE RECORD
  SIGNUP (
    CREATE vault_operators SET
      email = $email,
      display_name = $name,
      pass_hash = crypto::argon2::generate($pass),
      tenant_id = $tenant_id,
      role = 'operator'
  )
  SIGNIN (
    SELECT * FROM vault_operators
    WHERE email = $email
      AND crypto::argon2::compare(pass_hash, $pass)
      AND is_active = true
  )
  DURATION FOR TOKEN 1h, FOR SESSION 8h;

-- Operator table
DEFINE TABLE vault_operators SCHEMAFULL
  PERMISSIONS
    FOR select WHERE id = $auth.id
    FOR create, update, delete WHERE $auth.role = 'admin';

DEFINE FIELD email        ON vault_operators TYPE string;
DEFINE FIELD display_name ON vault_operators TYPE string;
DEFINE FIELD pass_hash    ON vault_operators TYPE string;
DEFINE FIELD tenant_id    ON vault_operators TYPE string;
DEFINE FIELD role         ON vault_operators TYPE string DEFAULT 'operator';  -- "admin" | "operator" | "viewer"
DEFINE FIELD is_active    ON vault_operators TYPE bool DEFAULT true;
```

### JWT Federation (External IdP)

```surql
-- Example: Azure AD integration
DEFINE ACCESS vault_azure_ad ON DATABASE TYPE JWT
  ALGORITHM RS256
  KEY "https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys"
  DURATION FOR SESSION 8h
  WITH AUTHENTICATE {
    LET $claims = $token;
    LET $tenant_id = $claims.tid OR 'default';
    RETURN $tenant_id IS NOT NONE;
  };
```

### Permission Model Summary

| Table | Read | Write | Delete | Who |
|-------|------|-------|--------|-----|
| `vault_tenants` | `$auth.role = 'admin'` | `$auth.role = 'admin'` | `NONE` | Admins only |
| `vault_secrets` | `tenant_id = $auth.tenant_id` | `tenant_id = $auth.tenant_id` | `tenant_id AND admin` | Tenant-scoped |
| `vault_tenant_keys` | `NONE` | `NONE` | `NONE` | System only |
| `vault_lock` | `true` | `true` | `NONE` | Any authenticated |
| `vault_audit` | `tenant_id = $auth.tenant_id` | `true` | `NONE` | Read: tenant. Write: any. |
| `vault_operators` | `id = $auth.id` | `role = 'admin'` | `role = 'admin'` | Self-read, admin write |

---

## Multi-Tenancy

### Isolation Levels

Two modes planned, similar to AeroDB's existing `TenancyStyle`:

| Mode | Mechanism | Isolation | Complexity |
|------|-----------|-----------|------------|
| **Conjoined** | `tenant_id` field on every secret row, `PERMISSIONS WHERE tenant_id = $auth.tenant_id` | Row-level (SurrealDB permissions) | Low |
| **Database-per-tenant** | Each tenant in own `DEFINE DATABASE` | Full database isolation (hard boundary) | Medium |

### Conjoined Mode (Default)

- All tenants share the same SurrealDB database.
- Row-level isolation via `PERMISSIONS`.
- Simpler to provision, suitable for smaller deployments.
- Inherits from the `TenancyStyle.Conjoined` pattern already in AeroDB.

### Database-per-Tenant Mode

- Each tenant gets its own SurrealDB database (e.g., `secrets_acme`, `secrets_beta`).
- Hard isolation — no cross-tenant query leakage possible.
- Requires `DEFINE DATABASE` privilege during tenant provisioning.
- Follows AeroDB's planned multi-database/schema support (Phase 17, gap #17).

### Tenant Resolution

Tenants are resolved from the authenticated principal:

```csharp
// Bearer token carries tenant claim
// JWT carries tenant_id claim
// Record user has tenant_id field

string? tenantId = $auth.tenant_id;
```

For the configuration provider, the tenant is resolved once at startup from the credential:

```csharp
builder.Configuration.AddSableVault(options =>
{
    options.Tenancy.Mode = TenantResolutionMode.ClaimBased;
    options.Tenancy.ClaimType = "tenant_id";
    // Or: explicit
    options.Tenancy.TenantId = "acme";
});
```

---

## Distributed Locking

### Purpose

When multiple instances of the vault admin service (or provisioning process) attempt to update the same configuration concurrently, distributed locking prevents:

- Race conditions on tenant key generation
- Conflicting secret updates
- Simultaneous schema migrations

### Implementation: SurrealDB Conditional UPDATE

The lock is acquired via a single atomic SurrealQL statement. Unlike SQL `SELECT ... FOR UPDATE`, SurrealDB does not support row-level locks in the traditional sense. Instead, we use a **conditional UPDATE with predicate** — if the predicate matches, the update succeeds (lock acquired); if not, the row was already locked (contention).

```surql
-- Acquire lock (idempotent, atomic)
LET $locked = (
    UPDATE ONLY vault_lock
    SET acquired_at = time::now(),
        owner = $owner,
        expires_at = time::now() + $timeout
    WHERE name = $lock_name
      AND (acquired_at IS NONE OR expires_at < time::now())
    RETURN AFTER
);

-- Only one caller will see $locked IS NOT NULL
```

```csharp
public async Task<IDistributedLock?> AcquireLockAsync(string lockName, TimeSpan timeout)
{
    var query = @"
        LET $locked = (
            UPDATE ONLY vault_lock
            SET acquired_at = time::now(), owner = $owner, expires_at = time::now() + $timeout
            WHERE name = $lock_name
              AND (acquired_at IS NONE OR expires_at < time::now())
            RETURN AFTER
        );
        RETURN $locked;
    ";

    var result = await _session.RawQueryAsync<VaultLockRecord?>(query, new Dictionary<string, object?>
    {
        ["lock_name"] = lockName,
        ["owner"] = _instanceId,
        ["timeout"] = $"{(int)timeout.TotalSeconds}s"
    });

    if (result.FirstOrDefault() is { } record)
    {
        return new DistributedLock(record, this);
    }

    return null;
}

public async Task ReleaseLockAsync(VaultLockRecord record)
{
    await _session.RawQueryAsync(
        "UPDATE vault_lock SET acquired_at = NONE, expires_at = NONE WHERE name = $name AND owner = $owner",
        new Dictionary<string, object?> { ["name"] = record.Name, ["owner"] = record.Owner }
    );
}
```

### Lock Heartbeat

Long-running operations should extend the lock periodically:

```csharp
// Every (timeout / 3) seconds, call:
await _session.RawQueryAsync(
    "UPDATE vault_lock SET expires_at = time::now() + $timeout WHERE name = $name AND owner = $owner",
    new Dictionary<string, object?> { ... }
);
```

### FusionCache Comparison

FusionCache uses a similar "try-acquire" pattern with a backplane (Redis). Here, SurrealDB serves as the backplane. The mechanism is:

1. **Try to acquire** via conditional UPDATE
2. **If acquired** → proceed with critical section
3. **If not acquired** → wait and retry with exponential backoff
4. **Release** by clearing the lock record

---

## API Surface

### `AddSableVault()` Extension

```csharp
public static class VaultConfigurationExtensions
{
    /// <summary>
    /// Adds the Sable Vault configuration provider to the configuration builder.
    /// Must be called after appsettings.json and environment variables to ensure
    /// vault secrets take priority.
    /// </summary>
    public static IConfigurationBuilder AddSableVault(
        this IConfigurationBuilder builder,
        Action<VaultOptions> configure)
    {
        var options = new VaultOptions();
        configure(options);
        return builder.Add(new SableVaultConfigurationSource(options));
    }
}
```

### `VaultOptions`

```csharp
public class VaultOptions
{
    /// <summary>Bootstrap configuration for initial connection and provisioning.</summary>
    public BootstrapOptions Bootstrap { get; set; } = new();

    /// <summary>Encryption key management provider.</summary>
    public EncryptionOptions Encryption { get; set; } = new();

    /// <summary>Multi-tenancy configuration.</summary>
    public TenancyOptions Tenancy { get; set; } = new();

    /// <summary>Cache configuration for secrets.</summary>
    public CacheOptions Cache { get; set; } = new();

    /// <summary>Reload secrets when they change on the server.</summary>
    public bool ReloadOnChange { get; set; } = true;

    /// <summary>Interval for polling secret changes (when ReloadOnChange is true).</summary>
    public TimeSpan ReloadInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Optional: custom ILoggerFactory.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }
}

public class BootstrapOptions
{
    /// <summary>Local embedded store for bootstrap config cache.</summary>
    public LocalStoreOptions? LocalStore { get; set; }

    /// <summary>SurrealDB endpoint. Required if no local store found.</summary>
    public string? Endpoint { get; set; }

    /// <summary>SurrealDB namespace.</summary>
    public string Namespace { get; set; } = "vault";

    /// <summary>SurrealDB database.</summary>
    public string Database { get; set; } = "secrets";

    /// <summary>Credentials for SurrealDB connection.</summary>
    public VaultCredentials Credentials { get; set; } = Credentials.FromEnvironment();

    /// <summary>If true, auto-provision schemas on first connect.</summary>
    public bool AutoProvision { get; set; } = true;

    /// <summary>If true, migrate bootstrap secrets into vault after provisioning.</summary>
    public bool MigrateOnProvision { get; set; } = true;
}

public class EncryptionOptions
{
    /// <summary>Key management service. Default: DpapiKeyManagementService.</summary>
    public IKeyManagementService KeyManagement { get; set; } = new DpapiKeyManagementService();

    /// <summary>Algorithm for data encryption. Default: AES-256-GCM.</summary>
    public string Algorithm { get; set; } = "AES-256-GCM";
}

public class TenancyOptions
{
    /// <summary>Tenant resolution mode.</summary>
    public TenantResolutionMode Mode { get; set; } = TenantResolutionMode.SingleTenant;

    /// <summary>JWT claim type for tenant ID (when Mode is ClaimBased).</summary>
    public string? ClaimType { get; set; } = "tenant_id";

    /// <summary>Explicit tenant ID (when Mode is Explicit).</summary>
    public string? TenantId { get; set; }
}

public enum TenantResolutionMode
{
    SingleTenant,   // Default tenant only
    Explicit,       // Explicitly set TenantId
    ClaimBased,     // Resolve from JWT claim
    HeaderBased     // Resolve from HTTP header (for gateway scenarios)
}
```

### Direct Vault Client (Non-ASP.NET)

```csharp
public interface ISableVault
{
    // Secret CRUD
    Task<string?> GetSecretAsync(string key, CancellationToken ct = default);
    Task SetSecretAsync(string key, string value, CancellationToken ct = default);
    Task DeleteSecretAsync(string key, CancellationToken ct = default);
    Task<bool> SecretExistsAsync(string key, CancellationToken ct = default);

    // Bulk
    Task<IDictionary<string, string>> ListSecretsAsync(string? prefix = null, CancellationToken ct = default);

    // Tenancy
    ISableVault ForTenant(string tenantId);

    // Key management
    Task RotateTenantKeysAsync(CancellationToken ct = default);
    Task RotateSecretKeyAsync(string key, CancellationToken ct = default);

    // Health
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
}

public static class SableVault
{
    public static async Task<ISableVault> CreateAsync(Action<VaultOptions> configure)
    {
        var options = new VaultOptions();
        configure(options);
        var vault = new SableVaultClient(options);
        await vault.InitializeAsync();
        return vault;
    }
}
```

---

## Project Structure

```
src/
  AeroDB.Sable.Vault/
    AeroDB.Sable.Vault.csproj          # New project
      → References: AeroDB.Sable, SurrealDb.Net
      → Targets: net10.0

    // Configuration Provider
    Configuration/
      SableVaultConfigurationProvider.cs    # ConfigurationProvider implementation
      SableVaultConfigurationSource.cs      # IConfigurationSource implementation
      VaultConfigurationExtensions.cs       # AddSableVault() extension methods
      VaultOptions.cs                       # Options class hierarchy

    // Encryption
    Encryption/
      IKeyManagementService.cs              # Pluggable KEK interface
      EnvelopeEncryptionService.cs          # AES-256-GCM encrypt/decrypt
      DpapiKeyManagementService.cs          # DPAPI-based KEK (default)
      AzureKeyVaultKeyManagementService.cs  # Azure Key Vault KEK (Phase 4)
      AwsKmsKeyManagementService.cs         # AWS KMS KEK (Phase 4)

    // Provisioning
    Provisioning/
      VaultProvisioner.cs                   # Self-provisioning orchestrator
      VaultSchemaManager.cs                 # DEFINE TABLE/ACCESS/PERMISSIONS
      BootstrapStore.cs                     # Local SurrealKv bootstrap read/write
      ProvisioningResult.cs                 # Result type with diagnostics

    // Secrets
    Secrets/
      SableVaultClient.cs                   # ISableVault implementation
      SecretStore.cs                        # CRUD operations on vault_secrets
      SecretCache.cs                        # In-memory cache with IChangeToken
      SecretRotator.cs                      # Background rotation daemon
      SecretSerializer.cs                   # Value ↔ byte[] conversion

    // Auth
    Auth/
      VaultAuthService.cs                   # Authentication + token management
      VaultCredentials.cs                   # Credential types (Bearer, User+PW, Token)
      AccessDefinitionBuilder.cs            # Fluent builder for DEFINE ACCESS

    // Tenancy
    Tenancy/
      TenantResolver.cs                     # Tenant resolution strategies
      ITenantResolver.cs                    # Pluggable resolver interface

    // Locking
    Locking/
      DistributedLockProvider.cs            # SurrealDB-based lock acquire/release
      IDistributedLock.cs                   # Lock interface (IDisposable)

    // Models
    Models/
      TenantKeyRecord.cs                    # vault_tenant_keys document
      SecretRecord.cs                       # vault_secrets document
      LockRecord.cs                         # vault_lock document
      AuditRecord.cs                        # vault_audit document
      EncryptedPayload.cs                   # ciphertext + metadata value object
      TenantRecord.cs                       # vault_tenants document

    // Diagnostics
    Diagnostics/
      VaultHealthCheck.cs                   # IHealthCheck implementation
      VaultTelemetry.cs                     # OpenTelemetry metrics/traces

tests/
  AeroDB.Sable.Vault.Tests/
    AeroDB.Sable.Vault.Tests.csproj
    Encryption/
      EnvelopeEncryptionServiceTests.cs     # Encrypt/decrypt round-trip
      DpapiKeyManagementServiceTests.cs     # Key wrap/unwrap
    Provisioning/
      VaultProvisionerTests.cs              # Idempotency, schema creation
      BootstrapStoreTests.cs                # Local store read/write
    Configuration/
      ConfigurationProviderTests.cs         # TryGet, Load, Reload
      ConfigurationIntegrationTests.cs      # End-to-end with WebApplication
    Secrets/
      SecretStoreTests.cs                   # CRUD, versioning, rotation
    Auth/
      VaultAuthServiceTests.cs              # Bearer token, record auth
    Tenancy/
      TenantResolverTests.cs                # Resolution modes
    Locking/
      DistributedLockProviderTests.cs       # Acquire/release, contention
```

---

## Open Questions & TBD

| # | Question | Status | Notes |
|---|----------|--------|-------|
| Q1 | **KEK sharing via SurrealDB key ring.** Can the master key be shared safely so any node can provision? Or should each node have its own KEK and share only wrapped IKs? | ⚠️ Needs threat model | Tentative approach: each provisioning node generates its own KEK; wrapped IKs are stored in SurrealDB and can be unwrapped by any node that has access to the same KEK provider (e.g., same Azure KV). For DPAPI, this means single-machine only. |
| Q2 | **Secret rotation strategy.** Should rotation happen on a schedule (background daemon)? On access (check-and-rotate)? Both? | ⚠️ TBD | Likely both: scheduled rotation for compliance, on-access for high-frequency secrets. |
| Q3 | **Client-side caching and staleness.** How long can a cached secret be used before it must be refreshed? What is the staleness tolerance? | ⚠️ TBD | Default: 5-minute reload interval. Overridable per secret via `rotation_policy`. |
| Q4 | **SurrealDB `LET` + conditional UPDATE atomicity.** Does the distributed lock pattern guarantee single-winner under concurrent access? | ⚠️ Needs verification | Must test with multiple concurrent clients. SurrealDB's transaction isolation may differ from PostgreSQL. |
| Q5 | **Human UI scope.** What does the Blazor admin dashboard need to support? List/create/update/delete secrets? Tenant management? Audit log viewer? User management? | ⚠️ TBD | Deferred to Phase 6. Scope will be defined in a separate spec. |
| Q6 | **Secret versioning.** Should old versions be retained? How many? For how long? | ⚠️ TBD | Likely: keep last N versions (configurable, default 10). Expire old versions after TTL. |
| Q7 | **Bootstrap security.** Is DPAPI-protected local SurrealKv sufficient for bootstrap? What about Docker containers where DPAPI machine scope differs between runs? | ⚠️ TBD | Docker containers: DPAPI user scope tied to container identity; may not survive container rebuild. Alternative: mount a persistent volume with a key file. |
| Q8 | **OpenTelemetry integration.** What metrics/traces should be emitted? | ⚠️ TBD | Minimum: secret access count, encryption/decryption latency, lock contention rate, provisioning health. |
| Q9 | **Cross-region / multi-cluster SurrealDB.** Does the vault work with SurrealDB's distributed capabilities (TiKV, FoundationDB)? | ⚠️ Not yet evaluated | SurrealDB distributed storage is in preview. Will evaluate when stable. |

---

## Implementation Phases

### Phase 0 — Foundation (Week 1-2)
**Goal:** Core project, encryption primitives, bootstrap flow.

| Deliverable | Details |
|-------------|---------|
| `AeroDB.Sable.Vault.csproj` | New project with references, CI integration |
| `IKeyManagementService` + `DpapiKeyManagementService` | Pluggable KEK interface, DPAPI default |
| `EnvelopeEncryptionService` | AES-256-GCM encrypt/decrypt with key wrapping |
| `EncryptedPayload` value object | Ciphertext + metadata serialization |
| `BootstrapStore` | Local SurrealKv store for bootstrap config |
| `VaultOptions` + `BootstrapOptions` | Configuration POCOs |
| Unit tests | Encryption round-trip, key wrap/unwrap, bootstrap store |

### Phase 1 — Configuration Provider (Week 3-4)
**Goal:** ASP.NET Core integration, secrets available via `IConfiguration`.

| Deliverable | Details |
|-------------|---------|
| `SableVaultConfigurationProvider` | `Load()`, `TryGet()`, `Set()`, `GetReloadToken()` |
| `SableVaultConfigurationSource` | `IConfigurationSource.Build()` |
| `VaultConfigurationExtensions.AddSableVault()` | Builder extension method |
| SurrealDB connection management | Client factory, session pooling |
| `SecretCache` | In-memory cache with IChangeToken support |
| Integration tests | WebApplication with secret resolution |

### Phase 2 — SurrealDB Provisioning (Week 5-6)
**Goal:** Self-provisioning to remote SurrealDB, schema creation, CRUD.

| Deliverable | Details |
|-------------|---------|
| `VaultProvisioner` | Full bootstrap flow orchestration |
| `VaultSchemaManager` | `DEFINE TABLE/ACCESS/INDEX/PERMISSIONS` |
| `SecretStore` | CRUD on `vault_secrets` table |
| `VaultAuthService` | Bearer token generation, basic auth |
| `AccessDefinitionBuilder` | Fluent API for DEFINE ACCESS |
| Idempotency | All provisioning operations re-runnable |
| Integration tests | Provision → write secret → read secret → verify |

### Phase 3 — Multi-Tenancy (Week 7-8)
**Goal:** Tenant isolation, RBAC integration.

| Deliverable | Details |
|-------------|---------|
| `TenantResolver` | All resolution modes (single, explicit, claim, header) |
| Conjoined tenancy | `PERMISSIONS WHERE tenant_id = $auth.tenant_id` |
| Database-per-tenant | `DEFINE DATABASE` per tenant |
| Tenant provisioning API | `ISableVault.ForTenant()` |
| Row-level security tests | Cross-tenant access rejection |

### Phase 4 — Advanced Features (Week 9-10)
**Goal:** Distributed locking, key rotation, cloud KMS.

| Deliverable | Details |
|-------------|---------|
| `DistributedLockProvider` | SurrealDB conditional UPDATE locking |
| `SecretRotator` | Background daemon for scheduled rotation |
| `AzureKeyVaultKeyManagementService` | Azure Key Vault KEK provider |
| `AwsKmsKeyManagementService` | AWS KMS KEK provider |
| `VaultHealthCheck` | `IHealthCheck` for SurrealDB connectivity |
| `VaultTelemetry` | OpenTelemetry metrics + traces |

### Phase 5 — Human Auth & Audit (Week 11-12)
**Goal:** Record user auth, audit trail, operator management.

| Deliverable | Details |
|-------------|---------|
| Record user auth | `DEFINE ACCESS TYPE RECORD` with signup/signin |
| JWT federation | `DEFINE ACCESS TYPE JWT` + JWKS for Azure AD/Auth0 |
| `VaultOperators` management | CRUD for operator accounts |
| `AuditRecord` + audit logging | Every access recorded to `vault_audit` |
| Audit query API | `ListAuditEntriesAsync(tenant, secret, timeframe)` |

### Phase 6 — Admin Dashboard (Future)
**Goal:** Blazor-based management UI (out of scope for initial delivery).

| Deliverable | Details |
|-------------|---------|
| Blazor Server dashboard | List/secrets, CRUD, tenant management |
| Secret history viewer | Version diff, rollback |
| Audit log viewer | Search, filter, export |
| Health dashboard | Connectivity, lock stats, rotation status |
| RBAC UI | Operator management, role assignment |

---

## References

### ASP.NET Core Configuration
- [Configuration in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0)
- [Custom configuration provider](https://learn.microsoft.com/en-us/dotnet/core/extensions/custom-configuration-provider)
- [Azure Key Vault Configuration Provider](https://learn.microsoft.com/en-us/aspnet/core/security/key-vault-configuration?view=aspnetcore-10.0)
- [Safe storage of app secrets in development](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0)
- [Building a Custom Configuration Provider for ASP.NET Core (MobileTonster)](https://mobiletonster.com/blog/code/building-a-custom-configuration-provider-for-aspnet-core)

### SurrealDB Security
- [SurrealDB Security Guide](https://surrealdb.com/docs/surrealdb/security)
- [DEFINE ACCESS](https://surrealdb.com/docs/surrealdb/surrealql/statements/define/access)
- [DEFINE USER](https://surrealdb.com/docs/surrealdb/surrealql/statements/define/user)
- [PERMISSIONS](https://surrealdb.com/docs/surrealdb/surrealql/statements/define/table#permissions)
- [crypto::argon2 module](https://surrealdb.com/docs/surrealdb/surrealql/functions/crypto)

### .NET Cryptography
- [Cross-platform cryptography in .NET](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography)
- [AesGcm class](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm)
- [ASP.NET Core Data Protection](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection)
- [CryptographicOperations.ZeroMemory](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.zeromemory)

### Reference Implementations
- [GoDaddy Asherah (application-layer envelope encryption)](https://github.com/godaddy/asherah)
- [Azure Key Vault configuration provider source](https://github.com/dotnet/aspnetcore/tree/main/src/Security/AzureKeyVault)
- [AWS Encryption SDK for .NET](https://github.com/aws/aws-encryption-sdk-dafny)

### Existing AeroDB Docs
- [Architecture overview](../design/architecture.md)
- [Marten API gap analysis](../design/gaps.md)
- [SurrealDB RecordId strategy](../spec/surrealdb-net-recordid-strategy.md)

---

> **This document is a living design artifact.** Decisions marked ⚠️ Tentative are subject to change as research continues and implementation reveals constraints. All feedback and revisions are tracked via the [Decision Registry](#decision-registry) above.
