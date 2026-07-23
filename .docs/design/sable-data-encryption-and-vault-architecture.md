# AeroDB.Sable Data Encryption and Vault Architecture

> **Status:** Architecture accepted; Phase B/C and embedded configuration V1 remain in AeroDB; Vault E1/E2a moved to AeroVault; whole-document Phase D deferred
> **Date:** 2026-07-20
> **Scope:** Sable document/field encryption first; dedicated Vault service second
> **Historical context:** [sable-vault-secrets-store.md](sable-vault-secrets-store.md) is brainstorming only
> **Related implementation:** [sable-embedded-configuration-store.md](sable-embedded-configuration-store.md) defines the separate in-process configuration package
> **Vault repository:** [microbian-systems/AeroVault](https://github.com/microbian-systems/AeroVault) now owns the Vault projects, tests, production-hardening roadmap, and a copy of this combined design context.

---

## 1. Purpose

This document defines a staged data-protection architecture for AeroDB.Sable.
The first deliverable is transparent application-layer encryption for mapped
Sable documents and fields. The longer-term deliverable is a dedicated
`AeroDB.Sable.Vault` service that owns key operations, secret policies, access
auditing, and secret lifecycle management.

These capabilities are related but are not the same security product:

1. **Sable data encryption** protects selected database values from database,
   backup, and storage compromise. In local mode, the application still owns
   the plaintext and can access the wrapping provider.
2. **Sable Vault** is a separate security boundary. Applications authenticate
   to the Vault service and never receive root wrapping keys or unwrapped data
   encryption keys.

The staged design intentionally lets AeroDB deliver useful field encryption
without pretending that an in-process encryption library has the same trust
boundary as a dedicated secrets service.

`AeroDB.Sable.Configuration` is a separately packaged, implemented in-process
SurrealKV configuration provider delivered before the dedicated Vault. It reuses the
envelope-encryption kernel, but it is not a Vault security boundary and does
not change the typed-client-first design of the later remote Vault service.

### 1.1 Current implementation boundary

The implemented Phase B/Phase C slices include `[Encrypt]` and
`EncryptField(...)` for `string` and `byte[]`, AES-256-GCM and
ChaCha20-Poly1305 envelopes with a fresh per-value DEK, mounted-file/local
AES-256-GCM key wrapping, active/previous KEK routing and explicit DEK rewrap,
source-generated attribute metadata, ordinary store/load/load-many/raw-query/
LINQ entity materialization, clear-field optimistic concurrency, startup
mapping validation, `ADB100` query diagnostics, and authoritative runtime
query guards.

The first Phase C slice adds optional HMAC-SHA-256 blind indexes for exact SSN
lookup: `[BlindIndex]`, fluent `.BlindIndex(...)`, source-generated metadata,
versioned active/previous key support, explicit idempotent active-key reindexing,
SCHEMAFULL sidecar fields and indexes, bound-token queries, post-decryption
fixed-time verification, and `ADB102` mapping diagnostics.
`UsSocialSecurityNumberV1` accepts ASCII digits plus spaces/hyphens and requires
exactly nine digits. The explicit SurrealDB hashing service keeps password
hashes separate from fast digests, uses a closed function catalog and bound
parameters, and can probe the connected server's capabilities.

Bulk, patch, batch, compiled-query, live-query, include/fetch, graph,
projection, search, spatial, time-series, multi-result raw query, snapshot, and
dirty-tracking paths currently reject encrypted document types before
persistence or materialization. Whole-document `.Encrypt()` is a deferred
Phase D TODO and is not a prerequisite for beginning the Vault service;
blind-index rewrite is an explicit maintenance operation rather than an
implicit side effect of a read.

The implemented Vault E1 foundation adds the separate
`AeroDB.Sable.Vault` and `AeroDB.Sable.Vault.SurrealDb` packages. It includes
canonical immutable secret paths, scoped default-deny policy evaluation,
byte-oriented owned plaintext, a distinct Vault AAD domain, sealed/readiness
key-provider behavior, immutable encrypted secret versions, optimistic
concurrency, and transactional local audit plus outbox persistence.

Vault E2a adds `AeroDB.Sable.Vault.Server`, an ASP.NET Core minimal-API hosting
package with real JWT bearer validation, a coarse Vault API permission,
Vault-owned issuer/client principal mapping, persisted workload identities,
immutable policy versions and tenant/environment bindings, raw byte secret
endpoints, bounded request bodies, canonical route rejection, RFC 7807 errors,
and uncacheable binary secret responses. HTTP integration tests use real
ephemeral RSA-signed access tokens through Alba; the end-to-end profile covers
JWT authentication, persisted policy resolution, encryption, audit, and
SurrealKV storage. Optional mTLS, authenticated remote-database runtime
permissions, RSA X.509 root wrapping, external audit export, OpenTelemetry, a
deployable composition host, and the typed remote client remain later work.

---

## 2. Goals

### 2.1 Initial goals

- Add fluent Sable mappings for reversible field encryption:

  ```csharp
  options.Schema.For<Customer>()
      .EncryptField(x => x.SocialSecurityNumber);
  ```

- Add whole-document encryption:

  ```csharp
  options.Schema.For<MedicalRecord>()
      .Encrypt();
  ```

- Support an optional Sable encryption algorithm parameter while keeping
  AES-256-GCM as the v1 default:

  ```csharp
  options.Schema.For<Customer>()
      .EncryptField(
          x => x.SocialSecurityNumber,
          EncryptionAlgorithm.Aes256Gcm);
  ```

- Encrypt before values cross the SurrealDB client boundary.
- Decrypt after raw database results return and before entity materialization.
- Use source-generated metadata and accessors instead of reflection-based field
  discovery.
- Preserve normal Sable entity usage for supported read and write operations.
- Make unsupported query, patch, index, and projection combinations fail
  clearly instead of silently producing incorrect behavior.
- Establish encryption provider contracts that can later be implemented by the
  dedicated Vault transit API without returning keys to the application.

### 2.2 Vault goals

- Run production Vault as a separate ASP.NET Core minimal API service.
- Authenticate workloads with externally issued OAuth 2.0 access tokens and
  optionally mutual TLS.
- Enforce tenant-scoped, default-deny policies.
- Store immutable secret versions using envelope encryption.
- Audit access and mutation without storing plaintext or key material.
- Support key rewrapping without decrypting and re-encrypting secret payloads
  where the provider permits it.

---

## 3. Non-goals

The Phase B field-encryption kernel will not provide:

- Transparent querying, sorting, grouping, or indexing over randomly encrypted
  plaintext.
- Deterministic encryption.
- Range, prefix, full-text, or order-revealing searchable encryption.
- Blind-index equality lookup until the separately staged Phase C capability is
  enabled.
- Encryption of SurrealDB record IDs, tenant-routing fields, concurrency
  fields, or relationship link fields.
- Encryption of arbitrary raw SurrealQL.
- Automatic protection for writes that bypass Sable's typed persistence
  pipeline.
- A claim that in-process encryption protects against a compromised
  application process.
- Cloud KMS, HSM, PKCS#11, Shamir unseal, or tenant-owned keys in the first
  slice.
- Dynamic database credentials, credential leases, or third-party credential
  rotation in Vault v1.

---

## 4. Terminology

### 4.1 Encryption

Encryption is reversible. An authorized caller can recover the original
plaintext by using the correct key and authenticated context.

Sable v1 uses AES-256-GCM authenticated encryption:

- 256-bit key
- 96-bit (12-byte) nonce
- 128-bit (16-byte) authentication tag
- Authenticated additional data (AAD)

### 4.2 Hashing

Hashing is one-way. It is not encryption and cannot hydrate the original value.

SurrealDB's current `crypto::*` database functions provide:

- Password hashing and comparison:
  - Argon2
  - bcrypt
  - PBKDF2
  - scrypt
- General hashes:
  - BLAKE3
  - JOAAT
  - MD5
  - SHA-1
  - SHA-256
  - SHA-512

They do not currently provide a general reversible AES encrypt/decrypt
function. The SurrealDB MCP also rejects `crypto::aes::encrypt(...)` as an
invalid function path.

For that reason, the APIs must remain semantically separate:

```csharp
// Reversible Sable application-layer encryption
mapping.EncryptField(
    x => x.SocialSecurityNumber,
    EncryptionAlgorithm.Aes256Gcm);

// One-way database hashing; proposed as a separate feature
mapping.HashField(
    x => x.PasswordHash,
    SurrealPasswordHash.Argon2);
```

The following shape is deliberately rejected because it calls hashing
“encryption” and implies that the original value can be materialized:

```csharp
// Rejected API
mapping.EncryptField(x => x.Password, SurrealCryptoType.Argon2);
```

Password-specific APIs must also define safe compare behavior and prevent
accidental re-hashing of an already hashed value. `HashField(...)` is therefore
adjacent work, not part of the first reversible-encryption slice.

#### 4.2.1 SurrealDB .NET client support

The pinned `surrealdb.net` submodule at commit
`4cf8041ea0a93073dadfcd6b799fba03427e196e` does not contain typed crypto or
hashing helpers. Source searches for SurrealDB crypto function paths and every
documented algorithm find no wrapper such as `Argon2Generate`,
`ComparePassword`, or `Sha256`.

The client does provide everything required for a Sable-owned typed adapter:

- `Query(...)` accepts an interpolated SurrealQL string and converts
  interpolated values into bound query parameters.
- `RawQuery(...)` accepts SurrealQL plus an explicit parameter dictionary.
- `SurrealDbResponse.EnsureAllOks()` validates the result set.
- `SurrealDbResponse.GetValue<T>(index)` materializes a scalar result.

For example, a Sable adapter can safely execute:

```csharp
var response = (await client.Query(
        $"RETURN crypto::argon2::generate({plaintext});",
        cancellationToken))
    .EnsureAllOks();

var encodedHash = response.GetValue<string>(0);
```

and verification:

```csharp
var response = (await client.Query(
        $"RETURN crypto::argon2::compare({encodedHash}, {candidate});",
        cancellationToken))
    .EnsureAllOks();

var matches = response.GetValue<bool>(0);
```

The function path must come from a closed
`SurrealHashAlgorithm`-to-SurrealQL mapping owned by Sable. It must never be
constructed from user input. Values remain parameters, require transport
encryption, and must not be logged.

Therefore no submodule change is required. Sable should provide its own typed
hashing facade over the client's parameterized query API and keep the raw
`SurrealDbResponse` out of the public Sable contract.

### 4.3 Data encryption key (DEK)

A DEK is a random symmetric key used to encrypt exactly one protected field
value or one whole-document payload version.

The DEK:

- Is generated with a cryptographically secure random number generator.
- Is never persisted in plaintext.
- Is used with AES-256-GCM to encrypt the value.
- Is wrapped by a key encryption key.
- Is cleared from temporary memory as soon as the operation completes.

### 4.4 Key encryption key (KEK)

A KEK is a longer-lived key owned by a configured key-wrapping provider. It
encrypts, or “wraps,” DEKs.

The KEK remains outside SurrealDB. SurrealDB stores only:

- Ciphertext
- Nonce
- Authentication tag
- Wrapped DEK
- Non-secret algorithm/provider/key identifiers

### 4.5 Envelope encryption

Envelope encryption separates payload encryption from long-lived key
management:

```text
plaintext
   |
   | AES-256-GCM with a fresh DEK
   v
ciphertext + nonce + tag

fresh DEK
   |
   | wrapped by provider KEK
   v
wrapped DEK
```

The ciphertext and wrapped DEK can live together in SurrealDB. Neither reveals
the plaintext without the external KEK.

---

## 5. Threat Model and Security Boundaries

| Threat | Sable local field encryption | Vault-backed field encryption | Vault secret service |
|---|---:|---:|---:|
| Stolen database files or backups | Protected | Protected | Protected |
| Compromised SurrealDB account | Protected ciphertext | Protected ciphertext | Protected ciphertext |
| Unauthorized database administrator | Protected ciphertext | Protected ciphertext | Protected ciphertext |
| Ciphertext moved to another record/field/tenant | Rejected by AAD | Rejected by AAD | Rejected by AAD |
| Compromised application process | Not protected | Authorized plaintext remains exposed, keys do not | Authorized plaintext remains exposed, keys do not |
| Compromised local key provider | Not protected | Not applicable when remote-only | Vault boundary must also be compromised |
| Compromised Vault service | Not applicable | Not protected | Not protected |
| Lost root wrapping key | Data is unrecoverable | Depends on Vault recovery | Data is unrecoverable |

Application-layer encryption does not remove plaintext from the application
that legitimately uses the data. It reduces the database's blast radius; it
does not make a compromised application trustworthy.

Production Vault adds a meaningful key boundary because the application calls a
transit-style protect/unprotect API. It does not receive the KEK or DEK.

---

## 6. Decision Registry

| ID | Decision | Status | Rationale |
|---|---|---|---|
| D1 | Production Vault is a separate service | Accepted direction | The trust boundary cannot be enforced by an in-process configuration provider |
| D2 | External OIDC/OAuth access tokens are the default workload identity; mTLS is optional | Accepted direction; details proposed | Reuses established identity infrastructure and supports stronger possession proof where needed |
| D3 | Vault secret paths are immutable in v1 | Accepted/implemented in E1 | Stabilizes AAD, policies, audit identity, caching, and version history |
| D4 | A root provider wraps a fresh DEK per field value/document version/secret version | Accepted/implemented for Vault versions | Small compromise radius and simple cryptographic reasoning |
| D5 | Successful secret reads and all mutations require a committed local audit event | Accepted/implemented for create/version/read in E1 | Prevents unrecorded successful disclosure and state change |
| D6 | Typed Vault client is primary; configuration provider is optional and later | Proposed | Avoids loading broad sets of long-lived plaintext strings |
| D7 | Vault secret values default to a 64 KiB maximum | Accepted direction | Limits memory amplification and misuse as blob storage |
| D8 | Sable field encryption is the first implementation slice | Accepted direction | Delivers immediate database-at-rest protection and establishes reusable crypto contracts |
| D9 | `RandomizedEnvelope` is the only reversible v1 field-encryption strategy; AES-256-GCM is the default and ChaCha20-Poly1305 is an explicit alternative | Accepted/implemented | Both algorithms are sound AEAD choices and share the authenticated-envelope pipeline |
| D10 | SurrealDB `crypto::*` hashing remains separate from encryption | Accepted/implemented | Hashes are one-way and require different storage/query semantics |
| D11 | Whole-document `.Encrypt()` uses the same envelope kernel but is deferred until after the initial Vault delivery unless explicitly reprioritized | Deferred/TODO | Whole-document mode has a much larger query and persistence compatibility surface and is not a prerequisite for a dedicated Vault trust boundary |
| D12 | Encrypted-query restrictions use analyzer, startup, and runtime enforcement | Accepted/implemented for field encryption | Compile-time diagnostics improve developer experience, while runtime checks cover dynamic configuration |
| D13 | `[Encrypt]` is an optional Sable-owned mapping attribute and does not inherit ASP.NET Identity personal-data attributes | Proposed | Encryption capability and personal-data classification are different concerns, and Sable core must not depend on ASP.NET Identity |
| D14 | `[Encrypt]` accepts a Sable reversible-encryption algorithm and defaults to AES-256-GCM; password hashing initially uses an explicit generate/verify service and transparent `[Hash]` persistence remains deferred | Accepted | Hashing is irreversible, and an automatic property transform cannot safely distinguish plaintext from an already encoded hash on a later save |
| D15 | Exact lookup of an encrypted field uses an explicit keyed blind index and query API | Accepted and initially implemented | Preserves randomized ciphertext while deliberately exposing only equality through a separately protected token |
| D16 | `SurrealHashAlgorithm` is an algorithm catalog; the explicit adapter probes actual SurrealDB capabilities before use | Accepted/implemented | The official client has generic query/result support but no typed crypto-function facade, server support can vary, and the submodule remains unchanged |
| D17 | Phase B supports encrypted `string` and `byte[]` fields with a stable application-assigned record ID; every other persistence/materialization path fails closed before serialization or query execution | Accepted | AAD binds the ciphertext to the record ID, and partial integration must not create plaintext bypasses |
| D18 | Generated storage metadata/surrogates carry encrypted envelopes; Sable never temporarily replaces a domain property with ciphertext | Accepted | Domain objects remain usable while plaintext cannot reach a database serializer accidentally |
| D19 | Cryptographic authentication, envelope-validation, provider-resolution, and key-resolution failures are stable security exceptions and are never translated to not-found or `null` | Accepted | Corrupt or substituted protected data must be distinguishable from an absent record |

---

## 7. Sable Fluent API

### 7.1 Field encryption

The primary mapping is:

```csharp
options.Schema.For<Customer>()
    .EncryptField(x => x.SocialSecurityNumber);
```

The planned richer per-field configuration is:

```csharp
options.Schema.For<Customer>()
    .EncryptField(x => x.SocialSecurityNumber, field =>
    {
        field.Algorithm = EncryptionAlgorithm.Aes256Gcm;
        field.Strategy = FieldEncryptionStrategy.RandomizedEnvelope;
        field.Provider = "local-envelope";
        field.MaxPlaintextBytes = 64 * 1024;
    });
```

The convenient enum overload may also be supported:

```csharp
options.Schema.For<Customer>()
    .EncryptField(
        x => x.SocialSecurityNumber,
        EncryptionAlgorithm.Aes256Gcm);
```

When that richer overload is added, `EncryptField(x => x.SomeField)` will be
shorthand for:

```csharp
mapping.EncryptField(x => x.SomeField, field =>
{
    field.Algorithm = EncryptionAlgorithm.Aes256Gcm;
    field.Strategy = FieldEncryptionStrategy.RandomizedEnvelope;
});
```

The algorithm and strategy are separate concepts:

- The **algorithm** identifies the authenticated cipher.
- The **strategy** defines how keys/nonces are produced and what query leakage
  is deliberately allowed.

`RandomizedEnvelope` generates a fresh DEK and nonce for every stored value.
It is the only v1 strategy, so every field configured by `EncryptField(...)` is
randomized unless a future strategy is explicitly selected.

The property selector must be a direct mapped property access. Nested property
paths, computed expressions, method calls, and multi-property anonymous objects
are rejected.

Expression trees are appropriate for selecting the member name, but they are
not the runtime encryption mechanism. The source generator must emit the
accessors and codecs used on persistence hot paths.

### 7.2 Whole-document encryption

```csharp
options.Schema.For<MedicalRecord>()
    .Encrypt();
```

An explicit form:

```csharp
options.Schema.For<MedicalRecord>()
    .Encrypt(document =>
    {
        document.Algorithm = EncryptionAlgorithm.Aes256Gcm;
        document.Provider = "local-envelope";
        document.MaxPlaintextBytes = 1024 * 1024;
    });
```

`.Encrypt()` means **encrypt the document payload as one envelope**. It does not
encrypt the physical SurrealDB table, its storage volume, or its intrinsic
record ID.

The following operational metadata stays clear so Sable can locate, isolate,
and safely update the record:

- SurrealDB record ID
- Tenant routing identifier, when present
- Optimistic concurrency version
- Soft-delete state
- Required Sable lifecycle metadata
- Encryption format/provider/key identifiers

All mapped domain payload fields are serialized with source-generated
`System.Text.Json` metadata and encrypted into a single envelope.

### 7.3 Mapping conflicts

Startup validation must reject:

- The same property configured as encrypted and indexed through an ordinary
  plaintext index. A generated keyed blind index is the only exception.
- The same property configured as encrypted and used as an identity,
  tenant-routing, concurrency, relationship, or discriminator field.
- Both whole-document encryption and individual field encryption on the same
  mapping.
- Whole-document encryption combined with domain-field indexes or server-side
  domain-field projections.
- A provider name that is not registered.
- An encryption algorithm unsupported by the current platform.
- A plaintext limit that exceeds the host safety ceiling.
- Unsupported property codecs or property shapes.

Failing at startup is safer than discovering the conflict after ciphertext has
been written.

### 7.4 Attributes

Sable should support a declarative field marker as an alternative to
`EncryptField(...)`:

```csharp
public sealed class Customer : Entity
{
    [Encrypt]
    public string SocialSecurityNumber { get; set; } = string.Empty;
}
```

The attribute is descriptive mapping metadata, so its CLR type should be
`EncryptAttribute` and normal usage should be `[Encrypt]`:

```csharp
[AttributeUsage(
    AttributeTargets.Property,
    AllowMultiple = false,
    Inherited = true)]
public sealed class EncryptAttribute : Attribute
{
    public EncryptAttribute(
        EncryptionAlgorithm algorithm =
            EncryptionAlgorithm.Aes256Gcm)
    {
        Algorithm = algorithm;
    }

    public EncryptionAlgorithm Algorithm { get; }

    public FieldEncryptionStrategy Strategy { get; init; }
        = FieldEncryptionStrategy.RandomizedEnvelope;
}
```

An enum is valid as a C# attribute constructor parameter, so both forms are
supported:

```csharp
[Encrypt] // Defaults to AES-256-GCM.
public string SocialSecurityNumber { get; set; } = string.Empty;

[Encrypt(EncryptionAlgorithm.Aes256Gcm)]
public string TaxIdentifier { get; set; } = string.Empty;
```

`EncryptAttribute` must derive directly from `System.Attribute`. It must not
inherit ASP.NET Core Identity's `PersonalDataAttribute` or
`ProtectedPersonalDataAttribute`.

Those Identity attributes classify data for Identity management and export/
deletion behavior. They do not define a Sable storage representation or query
capability. The sets also do not coincide:

- An encrypted field may contain a non-personal API credential or commercial
  secret.
- Personal data may intentionally remain clear because it must be indexed or
  queried.
- A field can independently be both `[PersonalData]` and `[Encrypt]`.

Inheriting an Identity attribute would also force the core `AeroDB.Sable`
package to depend on `Microsoft.Extensions.Identity.Core`. That dependency
belongs in `AeroDB.AspNetIdentity`, not in the general persistence package.

An optional `AeroDB.AspNetIdentity` convention may recognize
`[ProtectedPersonalData]` and configure Sable encryption for that field. Such a
convention must be explicit and documented; core Sable must not discover
Identity attributes by type-name matching.

The source generator cannot add an attribute to an already-declared property.
Source generators add declarations to the compilation; they do not rewrite the
user's syntax. Instead, the generator must normalize both of these inputs:

```csharp
[Encrypt]
public string SocialSecurityNumber { get; set; } = string.Empty;

// Or:
options.Schema.For<Customer>()
    .EncryptField(x => x.SocialSecurityNumber);
```

into the same generated encryption metadata:

```text
entity CLR type
property symbol and CLR name
storage field name
encryption strategy and algorithm
typed getter/setter and plaintext codec
query capabilities
```

The generated metadata is the shared authority used by the persistence
pipeline, startup validation, query translation, and analyzer manifest. Query
translation must not reflect over property attributes on each query.

The attribute should contain only model-stable intent and strategy. Provider
names, key identifiers, maximum host limits, and environment-specific choices
remain in fluent/host configuration. If the attribute and fluent mapping name
different strategies for the same property, generation or startup validation
must report a conflict rather than silently choosing one.

### 7.5 Reversible encryption versus hashing attributes

`[Encrypt]` accepts only algorithms that can restore plaintext. In v1 that is:

```csharp
public enum EncryptionAlgorithm
{
    Aes256Gcm = 1,
    ChaCha20Poly1305 = 2
}
```

The enum is Sable-owned because SurrealDB's documented `crypto::*` functions
are hashes, not general reversible field-encryption algorithms. Naming an
Argon2, bcrypt, PBKDF2, scrypt, SHA, or BLAKE3 option in
`EncryptionAlgorithm` would incorrectly promise that Sable can hydrate
the original value.

AES-256-GCM is the default because it is the most broadly deployed option and
has wide hardware acceleration. ChaCha20-Poly1305 is a modern alternative,
especially useful on platforms without fast AES hardware. Both are
authenticated-encryption-with-associated-data algorithms. Startup validation
must reject an explicitly selected algorithm when its .NET implementation
reports that the current platform does not support it.

One-way protection belongs to a distinct enum and API:

```csharp
public enum SurrealHashAlgorithm
{
    Argon2 = 1,
    Bcrypt = 2,
    Pbkdf2 = 3,
    Scrypt = 4,
    Blake3 = 5,
    Joaat = 6,
    Md5 = 7,
    Sha1 = 8,
    Sha256 = 9,
    Sha512 = 10
}

[Hash(SurrealHashAlgorithm.Argon2)]
public string Password { get; set; } = string.Empty;
```

`Argon2`, `Bcrypt`, `Pbkdf2`, and `Scrypt` are password hashing algorithms with
SurrealDB `generate`/`compare` functions. `Blake3`, `Joaat`, `Md5`, `Sha1`,
`Sha256`, and `Sha512` are fast general digests. The enum is an algorithm
catalog, not proof that the connected SurrealDB version supports every member.
Sable must pin or probe the supported capability set and reject unavailable
functions before use.

Phase B does not implement transparent `[Hash]` persistence. An entity property
cannot reliably communicate whether it contains new plaintext or an encoded
hash loaded from storage, so an automatic save transform can double hash an
unchanged value. Password protection starts with an explicit service:

```csharp
var passwordHash = await hashing.GenerateAsync(
    plaintext,
    SurrealHashAlgorithm.Argon2);
var matches = await hashing.VerifyAsync(candidate, passwordHash);
```

Password hashing exposes verification rather than decryption and must not allow
fast general hashes such as SHA-256 for passwords. The adapter maps compare
arguments in SurrealDB order: encoded hash first, candidate plaintext second.
`SurrealPasswordHash` retains the algorithm beside the encoded value so
algorithm upgrades do not depend on separate caller state. Plaintext candidates
may not pass through generic Sable or client debug-logging paths. Network
hashing requires HTTPS/WSS; externally supplied or in-process clients require
an explicit protected-transport assurance.

An SSN or another value that an authorized application must display uses
`[Encrypt]`, not `[Hash]`:

```csharp
public sealed class Customer : Entity
{
    [Encrypt]
    [PersonalData]
    public string SocialSecurityNumber { get; set; } = string.Empty;
}
```

On save, Sable encrypts the value with AES-256-GCM and persists only its
versioned envelope. On entity materialization, Sable authenticates and decrypts
the envelope before assigning the property. Authorization to load or display
the plaintext remains the consuming application's responsibility.

Randomized encryption still prevents direct server-side SSN equality queries.
The optional exact-lookup capability requires a separately keyed blind index
and an explicit query API; hashing the SSN with an ordinary fast digest is
unsafe because the input space is small enough to enumerate.

---

## 8. Encrypted Storage Shape

### 8.1 Field envelope

An encrypted property is stored as a versioned SurrealDB object:

```surql
social_security_number: {
    format: 1,
    algorithm: "A256GCM",
    wrapped_key: {
        provider: "local-envelope",
        key_id: "key-2026-07",
        algorithm: "A256GCMKW",
        data: <bytes>,
        metadata: { nonce: <bytes>, tag: <bytes> }
    },
    nonce: <bytes>,
    tag: <bytes>,
    ciphertext: <bytes>,
    codec: "utf8"
}
```

The original database field type changes from its plaintext type to the
envelope object type. Sable intercepts the value before entity
materialization, authenticates and decrypts it, decodes it, and then assigns
the normal CLR property value.

The envelope remains self-describing enough to support key rotation and
algorithm migration without relying on mutable application defaults.

### 8.2 Whole-document envelope

Whole-document storage uses clear operational metadata plus one payload:

```surql
{
    id: medical_record:123456789,
    tenant_id: "tenant-a",
    version: 7,
    is_deleted: false,
    _sable_encryption: {
        format: 1,
        algorithm: "A256GCM",
        wrapped_key: {
            provider: "local-envelope",
            key_id: "key-2026-07",
            algorithm: "A256GCMKW",
            data: <bytes>,
            metadata: { nonce: <bytes>, tag: <bytes> }
        },
        nonce: <bytes>,
        tag: <bytes>,
        ciphertext: <bytes>,
        codec: "json-stj-v1"
    }
}
```

### 8.3 Authenticated additional data

AAD is encoded as versioned, length-prefixed canonical binary structures with
published golden vectors. Payload AAD includes:

- Envelope format version
- Payload encryption algorithm identifier
- Namespace/database identity where needed
- Table name
- Record ID
- Tenant ID where present
- Field storage name, or the reserved whole-document marker
- Property codec identifier

The wrapped-DEK AAD additionally includes the key-wrapping algorithm, provider
identifier, and key identifier/version. Those routing values are intentionally
excluded from payload AAD so a DEK can be rewrapped under a new KEK without
changing payload ciphertext, nonce, or tag. They remain authenticated by the
wrapped-DEK tag.

Binding ciphertext to this context prevents an attacker from copying a valid
encrypted value to another tenant, record, or field.

For this reason:

- A record ID must exist before encryption.
- Renaming an encrypted mapped field requires a migration and re-encryption.
- Changing tenant ownership requires a controlled re-encryption operation.

---

## 9. Persistence Pipeline

### 9.1 Required central abstraction

The application-facing abstraction must protect data, not expose keys:

```csharp
public interface ISableDataProtectionProvider
{
    ValueTask<EncryptedEnvelope> ProtectAsync(
        ReadOnlyMemory<byte> plaintext,
        EncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<OwnedPlaintext> UnprotectAsync(
        EncryptedEnvelope envelope,
        EncryptionContext context,
        CancellationToken cancellationToken = default);
}
```

This level is intentional:

- A local provider can generate and wrap a DEK in-process.
- A future Vault transit provider can send plaintext over authenticated TLS to
  Vault and receive an envelope without returning a DEK.
- Callers cannot accidentally log, cache, or retain a raw DEK.

The lower-level key-wrapping contract belongs inside the local/Vault host
implementation:

```csharp
public interface IKeyWrappingProvider
{
    ValueTask<WrappedKey> WrapAsync(
        ReadOnlyMemory<byte> keyMaterial,
        KeyWrappingContext context,
        CancellationToken cancellationToken = default);

    ValueTask<OwnedKeyMaterial> UnwrapAsync(
        WrappedKey wrappedKey,
        KeyWrappingContext context,
        CancellationToken cancellationToken = default);
}
```

### 9.2 Write flow

For each protected field:

1. Validate mapping and plaintext size.
2. Encode the CLR value into bytes using its registered codec.
3. Require a stable record ID and resolve tenant/table/field context.
4. Generate a fresh 256-bit DEK.
5. Generate a fresh 12-byte nonce.
6. Encode canonical AAD.
7. Encrypt with AES-256-GCM and a 16-byte tag.
8. Wrap the DEK with the configured provider.
9. Clear plaintext staging buffers and the unwrapped DEK.
10. Send only the envelope to SurrealDB.

A new DEK for every value write makes nonce uniqueness easy to reason about and
limits a compromised DEK to one stored value version.

Phase B fixes the payload key, nonce, and tag sizes at 32, 12, and 16 bytes.
Envelope v1 assigns immutable numeric identifiers to the format, payload
algorithm, wrapping algorithm, provider, codec, and key version. The
key-wrapping format independently specifies its nonce, tag, canonical AAD,
maximum input size, and rotation behavior. `A256GCMKW` may not be written until
the implementation states whether it is the exact JWE algorithm or a
Sable-owned AES-GCM wrapping format.

### 9.3 Read flow

1. Read the envelope from SurrealDB.
2. Validate envelope format, algorithm, provider, and size before allocation.
3. Reconstruct canonical AAD from the current mapping and record context.
4. Resolve the provider recorded in the envelope.
5. Unwrap the DEK or call the Vault transit unprotect operation.
6. Authenticate and decrypt.
7. Decode into the mapped CLR property.
8. Clear temporary key and plaintext buffers after materialization.

Authentication-tag failure is a hard security error. Sable must not return
`null`, an empty value, or the ciphertext as a fallback.

### 9.4 Persistence paths that must be covered

Encryption cannot be added only to `DocumentSession.Store(...)`. The current
codebase has multiple serialization and materialization paths. The
implementation must either route each typed path through the central
transformation pipeline or explicitly reject encrypted mappings there.

The compatibility inventory includes:

- `Store` / `SaveChangesAsync`
- Insert, update, and upsert
- `LoadAsync`
- LINQ entity materialization
- Compiled queries returning entities
- Batched queries
- Bulk insert
- Typed patch operations
- Projections and aggregate snapshots
- Graph fetch/include materialization
- Change tracking and auditing
- Event-sourced snapshots

Raw SurrealQL and raw DTO projections do not receive automatic decryption
unless an explicit encrypted-envelope API is used.

The first implementation should support the normal document session and load
paths, then add other paths under explicit compatibility tests. Unsupported
operations must throw a stable `SableEncryptedOperationNotSupportedException`
before sending a query.

### 9.5 Source generation

The source generator should emit:

- Protected property metadata
- The source of the mapping (`[Encrypt]`, fluent mapping, or both)
- Storage field name
- Typed getter and setter delegates
- Plaintext codec
- Whether the field is required or optional
- Clear operational-field metadata for whole-document mode
- A document payload serializer/deserializer for whole-document mode
- A compile-time encryption manifest describing statically discovered encrypted
  fields, whole-document mappings, clear operational fields, and query strategy

This keeps reflection out of hot paths and enables Native AOT analysis.

---

## 10. Query Semantics

### 10.1 Why “randomized” matters

Not every conceivable encryption design has identical query behavior.
Queryability depends on what information the protection strategy deliberately
leaks.

| Protection strategy | Same plaintext produces same stored value? | Possible server-side operations | Leakage/cost | Status |
|---|---:|---|---|---|
| `RandomizedEnvelope` | No | None over plaintext | Only envelope size and metadata | V1 default and only v1 strategy |
| Randomized encryption plus blind index | Ciphertext: no; blind token: yes | Exact equality through a dedicated blind-index API | Equality and frequency patterns | Optional Phase C capability |
| Deterministic encryption | Yes within its cryptographic scope | Exact equality and equality index | Equality and frequency patterns; difficult safe key/nonce design | Not planned for v1 |
| Order-preserving/revealing encryption | Preserves ordering information | Equality, range, sort, min/max | Reveals ordering and substantial distribution information | Explicitly not planned |
| One-way password hash | Hash output verifies candidates | Algorithm-specific compare only | No recovery of original value | Separate `HashField` feature |

In the current design, **all fields configured with `EncryptField(...)` use
`FieldEncryptionStrategy.RandomizedEnvelope`**. The designation is stored in
the mapping, generated metadata/manifest, and encrypted envelope.

Fields marked with `[Encrypt]` have the same v1 strategy and restrictions.
The attribute and fluent API are two ways to declare one mapping; they are not
different encryption modes. Attribute-declared encryption and blind indexes
still require an explicit `options.Schema.For<T>()` registration. That
registration gives startup validation and schema generation a complete,
fail-closed inventory; the fluent call does not need to repeat the attribute
settings.

A future strategy must be explicitly selected. It must never silently weaken
an existing field from randomized encryption to a queryable strategy.

### 10.2 V1 query restrictions

A randomized encrypted field cannot participate in a server-side operation
that requires SurrealDB to understand or compare its plaintext, including:

- A `Where` predicate that reads the field
- `OrderBy` / `ThenBy`
- `GroupBy`
- `Distinct` over that field
- Equality, range, or string comparisons
- Unique, search, vector, or ordinary plaintext indexes
- `Min`, `Max`, `Sum`, `Average`, or another aggregate over the field
- Joins, graph traversal, or record links based on the field

Operations that do not inspect the encrypted field remain valid:

```csharp
// Valid: Id remains clear
var customer = await session.Query<Customer>()
    .SingleAsync(x => x.Id == customerId);

// Valid: counts rows rather than aggregating the protected value
var count = await session.Query<Customer>()
    .CountAsync(x => x.IsActive);
```

The following is invalid because SurrealDB sees only an envelope object:

```csharp
// Compile-time diagnostic when the encryption mapping is statically known;
// runtime query exception otherwise.
var customer = await session.Query<Customer>()
    .SingleAsync(x => x.SocialSecurityNumber == requestedSsn);
```

Selecting and materializing the full entity is supported.

A projection that includes an encrypted property requires one of:

1. Load and decrypt the entity, then project in the client.
2. A future explicit client-projection API.

Sable must not silently translate a plaintext comparison against ciphertext.

### 10.3 Exact equality lookup with a blind index

An SSN that must be displayed and searched requires two stored values:

```text
ssn       = randomized authenticated ciphertext envelope
ssn_bidx  = versioned HMAC-SHA-256 blind-index token
```

The blind-index key is separate from every encryption DEK and KEK and remains
outside SurrealDB. The token input is deterministic canonical data bound to its
scope:

```text
HMAC-SHA-256(
    blind-index key,
    namespace || database || tenant || table || field || normalized SSN)
```

For SSNs, normalization validates exactly nine digits and removes presentation
characters such as hyphens before computing the token. The original formatting
may remain inside the encrypted value.

Declarative mapping:

```csharp
[Encrypt]
[BlindIndex]
public string SocialSecurityNumber { get; set; } = string.Empty;
```

Equivalent fluent mapping:

```csharp
options.Schema.For<Customer>()
    .EncryptField(x => x.SocialSecurityNumber)
    .BlindIndex(
        x => x.SocialSecurityNumber,
        index =>
        {
            index.Algorithm = BlindIndexAlgorithm.HmacSha256;
            index.Normalizer = BlindIndexNormalizer.UsSocialSecurityNumberV1;
        });
```

Key configuration is separate from field-encryption configuration:

```csharp
options.Encryption.Provider = new AeadEnvelopeDataProtectionProvider(wrappingProvider);
options.Encryption.BlindIndexProvider = new HmacSha256BlindIndexProvider(
    activeKeyId: "customer-bidx-2026-07",
    activeKey: activeBlindIndexKey,
    previousKeys: previousBlindIndexKeys);
```

`HmacSha256BlindIndexProvider` copies supplied key material, requires at least
32 bytes per key, uses the active key for writes, and emits active plus at most
one immediately previous token for queries. It must be disposed to zero its
internal key copies.

Sable binds envelopes and blind tokens as protected query parameters rather
than embedding them in SurrealQL. Sable-created clients do not receive a client
logger. When `ClientFactory` supplies an external client for a protected
mapping, startup fails unless
`ExternalClientDisablesProtectedDataLogging = true`; set that assurance only
after disabling both query-value logging and CBOR serialization logging on the
external client. Password hashing additionally requires
`ExternalClientUsesProtectedTransport = true`. Plain HTTP/WS is rejected except
for an explicitly opted-in loopback development endpoint.

Search is deliberately explicit:

```csharp
var matches = await session.WhereEncryptedEqualsAsync<Customer>(
        x => x.SocialSecurityNumber,
        requestedSsn);

var customer = matches.SingleOrDefault();
```

Sable then:

1. Validates and normalizes `requestedSsn` in application memory.
2. Computes the versioned blind token with the active blind-index key.
3. Sends only the token as a parameter and queries the indexed `ssn_bidx`
   storage field.
4. Loads and decrypts the matching ciphertext.
5. Compares the normalized decrypted SSN in constant time before returning the
   entity, treating a mismatch as an integrity/configuration failure.

Ordinary LINQ equality over the encrypted property remains an analyzer error.
The dedicated method makes equality leakage visible and prevents Sable from
silently changing the security semantics of `[Encrypt]`.

A blind index reveals which rows have equal SSNs and their frequency. If both
the database and blind-index key are compromised, the small SSN domain can be
enumerated. Tenant/table/field scoping limits cross-context correlation but
does not remove that residual risk.

The initial API does not expose a `Unique` option. During key rotation, the
same logical SSN has different active and previous version tokens, so a
database uniqueness constraint would not enforce logical uniqueness. A future
uniqueness feature requires a rotation-safe logical constraint or migration
protocol; applications must not infer logical SSN uniqueness from the sidecar
index.

Blind-index lookup is a sensitive disclosure operation even though the
database receives only tokens. The consuming application or later Vault API
must authorize it separately from ordinary list/read access, rate-limit and
audit repeated probes, and avoid exposing a yes/no oracle to unauthenticated
callers. Sable's data layer cannot establish the caller's business
authorization boundary by itself.

Blind-index rotation stores a key version with the token. During rotation,
the implemented query path checks tokens for the active and configured previous
versions. `ReindexBlindIndexesAsync<T>()` is an explicit, idempotent maintenance
pass that authenticates/decrypts a bounded, tenant-scoped batch and rewrites
sidecars with the active key. Compare-and-set predicates detect concurrent
protected-field changes; the returned record-ID cursor supports stable progress
and resumption even if an earlier processed record is deleted. Reads never
mutate the database implicitly.
Blind-index keys must never be reused as encryption keys.

Whole-document encryption permits only operations over clear metadata such as
record ID, tenant ID, concurrency version, and soft-delete state.

### 10.4 Compile-time analyzer

The existing `AeroDB.Analyzers` project is the correct home for encryption
diagnostics. The analyzer should use Roslyn semantic operations rather than
method-name text matching.

Current and reserved diagnostics:

| Diagnostic | Severity | Condition |
|---|---|---|
| `ADB100` | Error | Attributed randomized encrypted field used by a server-side query operator, including predicate, projection, sort, group, distinct, or aggregate |
| `ADB101` | Error | `[Encrypt]` is applied to a property other than `string` or `byte[]` |
| `ADB102` | Error | `[BlindIndex]` is missing `[Encrypt]` or is applied to a non-string property |
| `ADB103` | Error | Whole-document encrypted type queries a domain field instead of clear operational metadata |
| `ADB104` | Error | Server-side projection directly selects an encrypted property in an unsupported shape |
| `ADB105` | Warning | Encryption configuration cannot be determined statically; runtime validation remains authoritative |
| `ADB106` | Error | An encryption or blind-index attribute contains an undefined enum value |
| `ADB107` | Error | `[Encrypt]`, `EncryptField(...)`, or `Encrypt(...)` targets the document identity |

Examples:

```csharp
// ADB100
session.Query<Customer>()
    .Where(x => x.SocialSecurityNumber == ssn);

// ADB100
session.Query<Customer>()
    .OrderBy(x => x.SocialSecurityNumber);

// ADB101
public sealed class InvalidCustomer
{
    [Encrypt]
    public int SecretNumber { get; set; }
}

// ADB102
public sealed class InvalidBlindIndex
{
    [BlindIndex]
    public string SocialSecurityNumber { get; set; } = string.Empty;
}
```

The analyzer must distinguish server-side query expressions from explicit
client-side work:

```csharp
// Allowed: materialize/decrypt first, then evaluate in application memory.
var customers = await session.Query<Customer>()
    .Where(x => x.IsActive)
    .ToListAsync();

var match = customers.SingleOrDefault(
    x => x.SocialSecurityNumber == requestedSsn);
```

This is functionally valid but may be inefficient or expose more plaintext than
necessary. A separate performance/security advisory diagnostic may be added
later; it is not an execution error.

### 10.5 Static-discovery boundary

Compile-time enforcement is strongest when the mapping is visible as a direct
attribute or expression:

```csharp
[Encrypt]
public string SocialSecurityNumber { get; set; } = string.Empty;

// Equivalent fluent declaration:
options.Schema.For<Customer>()
    .EncryptField(x => x.SocialSecurityNumber);
```

An explicit `[Encrypt]` attribute is especially useful across assembly
boundaries because it remains attached to the property symbol in referenced
metadata. The source generator should collect attribute and direct fluent
mappings and emit a compile-time encryption manifest. The analyzer can combine
property attributes, that manifest, and Roslyn invocation/operation analysis to
find incompatible LINQ and mapping operations across the compilation.

An analyzer cannot prove arbitrary runtime behavior, including:

- Conditional mapping based on configuration or environment
- Reflection or dynamically constructed expressions
- Encryption registration hidden in opaque helper code
- Mapping supplied by a plugin loaded at runtime
- Raw SurrealQL strings
- A mapping defined in another assembly without a published generated manifest

Therefore enforcement has three layers:

1. **Compile time:** `AeroDB.Analyzers` reports statically provable misuse.
2. **Startup:** mapping validation rejects incompatible encryption, index, and
   relationship configuration.
3. **Query translation:** Sable inspects runtime encryption metadata and throws
   `SableEncryptedOperationNotSupportedException` before sending SurrealQL.

The runtime layer is the security/correctness authority. The analyzer is an
early developer-experience guard, not the only guard.

The analyzer diagnostic and runtime exception must identify the entity,
property, strategy, attempted operation, and safe alternatives. For example:

```text
ADB100 / SableEncryptedOperationNotSupportedException:
Customer.SocialSecurityNumber uses RandomizedEnvelope encryption and cannot be
used in a server-side Where equality comparison. Query by a clear operational
field, materialize before comparing in application memory, or use a future
explicit blind-index API for equality lookup.
```

The runtime exception should also expose these values as structured properties
so logs and tests do not have to parse the message.

---

## 11. Key Provider Path

### 11.1 Initial local provider

The first implementation may use a mounted-file KEK provider for local,
single-service, and development deployments.

Requirements:

- Key file is outside the database and source tree.
- File-system ACLs limit access to the application identity.
- Provider exposes a stable provider name and key ID.
- Old keys remain available for decryption until all values are rewrapped or
  re-encrypted.
- Missing or unreadable root material fails startup/readiness.
- Keys and secret material are never written to logs.

This mode protects the database but not a compromised application host.

### 11.2 X.509 provider

An RSA X.509 provider may wrap DEKs with RSA-OAEP-SHA256.

V1 should reject ECDSA-only certificates because they cannot perform RSA key
wrapping. Certificate private-key loading and platform behavior require
cross-platform integration tests.

### 11.3 Windows DPAPI

DPAPI is optional and Windows-specific. It is useful for development or
single-machine deployments but cannot be the cross-platform default.

### 11.4 Vault transit provider

The future remote provider calls:

```text
POST /v1/transit/encrypt
POST /v1/transit/decrypt
```

The application sends the value and authenticated context over TLS. Vault
performs envelope encryption and returns an envelope or plaintext response.
The application never receives the DEK or root provider capability.

Batch transit operations may later reduce network overhead for documents with
multiple protected fields.

---

## 12. Vault Workload Authentication

### 12.1 OIDC/OAuth access tokens

OpenID Connect establishes identities and OAuth 2.0 access tokens authorize API
access. Workloads present an access token to Vault using the bearer scheme.
Vault must not accept an OIDC ID token as an API access token.

Vault validates:

- Signature
- Trusted issuer
- Vault-specific audience
- Expiration and not-before time
- `at+jwt` access-token type by default
- Exactly one client identity (`client_id`, `azp`, or `appid`)
- A subject equal to that client identity for the default workload-only profile
- Required scopes
- Replay identifier where the issuer supplies one

Vault then maps the verified issuer plus client ID to a `WorkloadPrincipal`.
The default profile rejects delegated user tokens and generic `typ=JWT` tokens;
provider-specific profiles belong in the deployable host and must retain an
unambiguous, Vault-owned workload identity. A caller-supplied `tenant_id` claim
is not sufficient on its own. The principal must have a Vault policy binding
allowing access to the requested tenant and path.

Advantages:

- Works with existing identity providers and workload identity platforms.
- Short-lived credentials.
- Central revocation and lifecycle policy.
- Familiar ASP.NET Core validation and authorization pipeline.

Trade-offs:

- Bearer tokens can be replayed if stolen until they expire.
- Issuer/JWKS availability and rotation must be operationally managed.
- Claims can become an accidental policy language if not normalized into
  Vault-owned principals and bindings.

### 12.2 Mutual TLS

With mTLS, the client proves possession of a private key during the TLS
handshake. Vault maps the verified client certificate, SAN, SPIFFE ID, or
approved public-key identity to a workload principal.

Advantages:

- Strong possession proof.
- No reusable bearer token is sent by itself.
- Natural fit for service mesh and high-trust internal workloads.

Trade-offs:

- Certificate issuance, renewal, revocation, and trust bundles add operational
  complexity.
- TLS termination at a proxy requires trusted certificate forwarding or
  validation at the proxy.
- Client certificates are negotiated per connection, so optional mTLS on
  selected routes is awkward. A dedicated hostname/listener is safer.

### 12.3 Recommended v1 composition

- OIDC/OAuth access tokens are the default and required first path.
- mTLS is an optional second authentication scheme for tightly controlled
  workloads.
- High-security deployments may require both a valid access token and a mapped
  client certificate.
- Vault authorization always evaluates its own policies after authentication.
- SurrealDB authentication is private infrastructure between Vault and its
  database; it is not exposed to workloads.

---

## 13. Immutable Vault Secret Paths

A Vault secret path is the stable security identity of a secret:

```text
payments/stripe/api-key
```

The display name, labels, expiry, enabled state, and policy metadata may change.
The canonical path does not change in v1.

Benefits:

1. **AAD stability.** If path is authenticated, renaming would require
   re-encrypting every historical version.
2. **Policy stability.** Exact and prefix rules keep referring to the same
   object.
3. **Audit clarity.** Historical events cannot appear to refer to a different
   secret after a rename.
4. **Cache correctness.** Cache keys and invalidation identities remain stable.
5. **No alias ambiguity.** There is one canonical resource identity.
6. **Safer replication.** Ciphertext cannot be copied to another path and still
   authenticate.

The v1 “rename” workflow is:

1. Create a new secret at the desired path.
2. Copy or set the value as a new secret under authorization.
3. Update consumers and policies.
4. Disable the old secret.
5. Retain its version and audit history according to policy.

A later alias feature can provide friendly redirection without changing the
cryptographic identity.

---

## 14. Vault Secret Versioning and DEKs

Each secret write creates:

- One immutable `VaultSecretVersion`
- One fresh random DEK
- One AES-GCM ciphertext/nonce/tag set
- One wrapped DEK

Why use a DEK per version:

- Compromise of one DEK exposes one version, not a tenant or Vault.
- Every write naturally uses a new key, avoiding nonce-reuse bookkeeping.
- Root-key rotation can rewrap the small DEK instead of re-encrypting the
  potentially larger payload.
- Multiple wrapping providers and key IDs can coexist during migrations.
- The database never needs the root key.

Two distinct rotation operations must be named clearly:

1. **Rewrap:** unwrap the existing DEK and wrap it with a new KEK. Ciphertext
   remains unchanged.
2. **Re-encrypt:** decrypt the secret and create new ciphertext with a new DEK.

Losing the KEK and every recoverable copy of it makes all DEKs wrapped by that
key unusable. Backup and recovery procedures must test restoration of both
database data and key-provider material.

The v1 hierarchy is deliberately:

```text
root provider KEK -> per-version DEK
```

A tenant intermediate key is deferred until tenant cryptographic erasure or
cloud-KMS economics justify the additional hierarchy.

---

## 15. Vault Audit: Fail-Closed Semantics

### 15.1 Successful read

For a successful secret read:

1. Authenticate the principal.
2. Authorize tenant, action, and path.
3. Read the encrypted version.
4. Decrypt into owned temporary memory.
5. Append the local audit event.
6. Commit the audit operation.
7. Only then return plaintext to the caller.

If the audit commit fails, Vault clears the plaintext buffer and returns a
service error. It does not release an unaudited secret.

This is “fail closed”: losing the ability to prove a successful disclosure also
loses the ability to disclose.

### 15.2 Mutation

Secret mutation, current-version update, and the audit event occur in one
database transaction. Either all commit or none commit.

### 15.3 Denial and failure

Denied requests never receive plaintext. Vault still attempts to record the
denial. If denial auditing also fails, Vault returns the denial and emits a
high-severity operational signal; it must not turn an authorization denial into
success.

### 15.4 Availability trade-off

Fail-closed auditing can turn audit-storage failure into a Vault outage. That is
intentional for a secrets system but requires:

- Capacity isolation
- Back-pressure
- Health and readiness checks
- Alerting
- Tested recovery

### 15.5 Integrity boundary

A table in the same SurrealDB database is not independently non-repudiable
against a database owner.

The defensible v1 design is:

- Only the Vault runtime identity inserts local audit events.
- Applications cannot connect to the Vault database.
- Local audit participates in the critical transaction.
- An outbox exports audit events to an external append-only sink.
- Optional signing or hash chaining provides tamper evidence.

Audit records never include plaintext, ciphertext, wrapped keys, tokens, or
request bodies.

---

## 16. Typed Vault Client Before Configuration Provider

### 16.1 Typed client

The primary client is explicit:

```csharp
await using var secret = await vault.GetAsync(
    new VaultScope(vaultId, tenantId, environment),
    SecretPath.Parse("payments/stripe/api-key"),
    cancellationToken);
```

Benefits:

- The application requests only the secret it needs.
- Authentication, authorization, and auditing happen per access.
- The returned value can own a disposable byte buffer.
- Call sites make secret access visible during review.
- A value need not remain in a global configuration dictionary.
- The API can carry an expected version, expiry, and content type.

`SecretValue` should be byte-first, implement `IDisposable` or
`IAsyncDisposable` only where ownership requires it, clear owned memory, and
never reveal contents through `ToString()`.

The CLR cannot guarantee erasure of every copy after a secret becomes a
`string`. Debuggers, crash dumps, serializers, and application code may create
additional managed copies. The API must state that limitation honestly.

### 16.2 Configuration provider

`IConfiguration` is string-based and intentionally keeps values available for
the application's lifetime. A configuration provider commonly:

- Loads many values at once.
- Stores them in a shared dictionary.
- Makes them broadly available through dependency injection.
- Creates additional strings during binding and reload.
- Cannot reliably zero immutable strings.

Therefore, configuration integration is an optional convenience adapter, not
the security core.

If added, it must:

- Call Vault over HTTPS, never SurrealDB.
- Be read-only.
- Require explicit key allowlists or narrowly scoped prefixes.
- Never load every secret by default.
- Document the longer plaintext lifetime.
- Avoid implementing `Set(...)` as a remote write side effect.

---

## 17. Secret Size Limits

The 64 KiB Vault default is an operational limit, not an AES-GCM limitation.

It exists because a secret request may temporarily allocate:

- Incoming encoded request bytes
- Decoded plaintext
- Ciphertext
- Authentication metadata
- HTTP/client buffers
- Audit and validation structures

A bounded value reduces memory-amplification and denial-of-service risk. It also
discourages using Vault as blob or document storage.

Recommended policy:

- Vault default maximum: 64 KiB plaintext
- Deployments may configure a lower limit
- Raising the limit requires explicit host configuration
- A separate hard safety ceiling should prevent accidental unbounded values
- Larger material belongs in object/blob storage with Vault holding the
  wrapping key or access credential

Sable field/document encryption has a separate configurable plaintext limit
because domain documents may legitimately be larger. Whole-document encryption
should initially use a conservative 1 MiB default and reject larger payloads
until streaming or chunked protection is designed.

---

## 18. Vault Domain and Storage Model

### 18.1 Aggregates

- `VaultSecret`
  - Stable identity and immutable canonical path
  - Tenant and Vault ownership
  - Current-version pointer
  - Enabled/disabled/deleted status
  - Optimistic concurrency version
- `VaultSecretVersion`
  - Immutable encrypted payload
  - Provider/key metadata
  - Creation and expiry metadata
- `VaultPolicy`
  - Versioned exact/prefix rules
  - Default deny; explicit deny wins
- `WorkloadPrincipal`
  - Normalized external identity
- `VaultPolicyBinding`
  - Connects principals to allowed tenant/path/action scopes
- `VaultAuditEvent`
  - Append-only local security event
- `VaultAuditOutbox`
  - External audit delivery
- `VaultRotationJob`
  - Manual/background rewrap and later re-encryption work

### 18.2 Principal tables

Applications do not receive Vault database credentials. Vault has:

- A privileged provisioning/migration identity
- A separate least-privileged runtime identity

The runtime identity must not be a database owner.

### 18.3 Transactions

Creating a version:

1. Read the `VaultSecret` current pointer and concurrency value.
2. Allocate the next version under transaction control.
3. Insert immutable `VaultSecretVersion`.
4. Update the current pointer conditionally.
5. Insert `VaultAuditEvent`.
6. Commit.

Write conflicts are retried with a new version calculation and fresh
encryption context.

---

## 19. Operational States

Vault exposes:

- **Starting:** process is initializing.
- **Sealed:** wrapping provider is unavailable or deliberately disabled.
- **Ready:** database, wrapping provider, and local audit write path are
  operational.
- **Degraded:** core secret operations work but a non-critical dependency, such
  as external audit export, is behind.

Liveness checks only whether the process can continue running. Readiness checks
whether Vault can safely satisfy a request.

Provider unavailability does not permit plaintext fallback, stale decrypted
cache fallback, or bypassed auditing.

---

## 20. Implementation Path

### Phase A — Contract and threat-model foundation

- Accept or revise the proposed decisions in this document.
- Define envelope binary/object format v1.
- Define stable encryption error types.
- Define provider and plaintext ownership contracts.
- Define compatibility behavior for every Sable persistence path.
- Reserve encryption analyzer diagnostic IDs and define their severity/meaning.
- Add a secure authenticated SurrealDB integration-test profile.

### Phase B — Sable field encryption kernel

- Add mapping metadata and startup validation.
- Extend source-generated metadata with field codecs/accessors.
- Generate the compile-time encryption manifest.
- Add `AeroDB.Analyzers` mapping/query diagnostics.
- Implement AES-256-GCM envelope protection.
- Support `string` and `byte[]` encrypted fields only.
- Require record IDs to be allocated before encryption.
- Generate storage metadata/surrogates so plaintext domain properties are never
  sent to the SurrealDB serializer.
- Implement a test wrapping provider, then the mounted-file wrapping provider.
- Integrate explicit store/insert/update/upsert, normal load, and ordinary LINQ
  entity materialization paths.
- Add one asynchronous pre-materialization transform used by every supported
  typed read.
- Add tamper, cross-record, cross-field, and cross-tenant tests.
- Block bulk, patch, batch, compiled, live, graph/include, projection, snapshot,
  and dirty-tracking paths explicitly before serialization or query execution.
- Prove protected plaintext does not appear in Sable or client request logs.

### Phase C — Persistence completeness

- Implement ChaCha20-Poly1305 behind platform capability validation and run the
  same conformance suite used by AES-256-GCM.
- Integrate bulk operations and typed patches.
- Integrate compiled/batched query materialization.
- Define projection, event snapshot, and graph behavior.
- Ensure change tracking and logging redact protected values.
- Add explicit plaintext-to-envelope migration tooling.
- Add optional blind-index storage, rotation, and explicit equality lookup.
- Add X.509 and optional Windows DPAPI providers.

### Phase D — Whole-document encryption (deferred TODO)

This phase is intentionally deferred and does not block Phase E. Resume it
after the initial Vault delivery unless it is explicitly reprioritized.

- Add `.Encrypt()` mapping.
- Generate whole-document payload codecs.
- Enforce clear operational-field rules.
- Block incompatible domain-field queries and indexes.
- Add size and memory-pressure tests.

### Embedded configuration package — implemented before Phase E

- Added the separate `AeroDB.Sable.Configuration` package.
- Added persistent embedded SurrealKV storage with Sable-encrypted values.
- Added async provisioning/CRUD and a read-only `IConfiguration` provider.
- Added explicit reload tokens compatible with `IOptionsMonitor`.
- Added database-path client sharing, Windows file-lock retry, bootstrap
  validation, and fail-closed key/record binding.
- Added focused TUnit/Shouldly persistence, encryption, JSON/provider-order,
  options-monitor, tamper, and failure-path coverage.

### Phase E1 — Vault foundation (implemented in AeroVault)

- Added `AeroDB.Sable.Vault` domain/use-case package.
- Added `AeroDB.Sable.Vault.SurrealDb` runtime persistence and privileged
  schema-provisioning package.
- Added immutable canonical paths, scoped default-deny policy evaluation, and
  explicit-deny precedence.
- Added a distinct Vault-secret AAD domain bound to Vault, tenant, environment,
  path, and version.
- Added sealed/ready mounted-file or custom root-provider lifecycle.
- Added immutable encrypted versions and optimistic concurrency.
- Added atomic mutation/audit/outbox transactions and fail-closed read audit.
- Added focused domain, crypto, service, real SurrealKV transaction, rollback,
  tamper, and failure-path tests.

### Phase E2a — Authenticated Vault HTTP boundary (implemented in AeroVault)

- Added the `AeroDB.Sable.Vault.Server` ASP.NET Core minimal-API hosting package.
- Added real JWT bearer validation for signature, trusted issuer, Vault
  audience, lifetime, strict `at+jwt` type, exact client-credentials identity,
  issued-at, replay identifier, and a coarse scope/role permission.
- Added exact Vault-owned external identity mapping; token tenant/path/action
  claims never create authorization.
- Persisted workload principals, external identities, immutable policy
  versions, and tenant/environment policy bindings in SurrealDB.
- Added raw-byte create, version, and read endpoints with a 64 KiB hard ceiling,
  optimistic `If-Match`, no JSON/Base64 secret envelopes, and uncacheable
  `application/octet-stream` disclosure.
- Added ambiguous path rejection, generic RFC 7807 failures, HTTPS-required
  default hosting, passive stored content-type metadata, and sensitive-payload
  endpoint metadata.
- Added real-JWT Alba coverage plus a full
  JWT-to-policy-to-encryption-to-audit-to-SurrealKV exercise.

### Phase E2b — Production service hardening (next planned phase in AeroVault)

- Add a deployable composition host and authenticated, least-privileged
  SurrealDB runtime/provisioning identities and permissions.
- Add optional mTLS scheme on a dedicated listener or trusted termination
  boundary.
- Add RSA X.509 root provider.
- Add external audit outbox exporter and OpenTelemetry without payload capture.
- Add rate limits, replay-cache integration, issuer/JWKS outage exercises, and
  production logging/redaction verification.

### Phase F — Vault clients and transit

- Add typed Vault client.
- Add Vault transit implementation of the Sable data-protection provider.
- Add manual rewrap jobs and recovery tooling.
- Add optional read-only configuration provider after the direct client is
  proven.

---

## 21. Testing Requirements

Use TUnit, Shouldly, NSubstitute, AutoFixture/Bogus where useful, and Alba for
Vault HTTP integration tests.

### 21.1 Crypto tests

- Known-answer and round-trip tests
- Fresh ciphertext for repeated equal plaintext
- Tampering with every envelope component fails
- Wrong table/record/tenant/field AAD fails
- Unknown format/algorithm/provider/key fails closed
- Truncated and oversized envelope rejection
- Key and owned-plaintext disposal behavior

### 21.2 Sable mapping tests

- Direct property selector accepted
- Computed/nested selector rejected
- Identity/tenant/version/link encryption rejected
- Index/query configuration conflict rejected
- Field schema changes to envelope storage type
- Source-generated accessor and codec coverage
- `[Encrypt]` and `EncryptField(...)` produce equivalent resolved encryption
  descriptors
- `[Encrypt]` is discoverable by an analyzer in a referencing project
- `[Encrypt]` and `[Encrypt(Aes256Gcm)]` resolve to the same v1 algorithm
- AES-256-GCM round-trips and rejects tampering
- ChaCha20-Poly1305 runs the authenticated-envelope conformance suite
- Unsupported encryption algorithms fail startup capability validation
- Hash algorithms are rejected as `[Encrypt]` arguments
- `SurrealHashAlgorithm` covers every documented SurrealDB crypto hash
- Password hashes and fast digests retain distinct generated capabilities
- Transparent `[Hash]` persistence remains deferred to avoid double hashing
- The Sable hashing adapter maps every enum value to a fixed SurrealQL function
- Hash generation and comparison use bound parameters, not string concatenation
- SurrealDB hashing failures are translated into stable Sable exceptions
- Blind-index normalization is stable and rejects malformed SSNs
- Blind-index lookup sends only the token to SurrealDB
- Blind-index results are decrypted and verified before being returned
- Blind-index key rotation supports active and previous token versions
- Compatible attribute and fluent settings merge without duplicate mappings
- Conflicting attribute and fluent strategies report a deterministic diagnostic
- Attribute-declared protection without `Schema.For<T>()` fails closed
- Undefined encryption/blind-index enum values report `ADB106` and never
  silently downgrade to a default
- Identity personal-data classification does not implicitly encrypt in Sable
- The optional ASP.NET Identity convention is tested independently when enabled

### 21.3 Analyzer tests

- `Where` comparison over randomized encrypted field reports `ADB100`
- Sort/group/distinct/aggregate over an attributed encrypted field reports
  `ADB100`
- Unsupported `[Encrypt]` property types report `ADB101`
- Fluent-only mappings remain protected by the runtime guard
- Query by clear ID/tenant/version metadata produces no diagnostic
- Row `Count` over an unencrypted predicate produces no diagnostic
- Query after explicit materialization produces no error diagnostic
- Dynamically undiscoverable configuration produces the documented warning or
  defers cleanly to runtime validation
- Diagnostic locations point to the protected member access
- Analyzer recognizes generated manifests from referenced projects

### 21.4 Persistence-path tests

- Insert/update/upsert/load
- LINQ entity materialization
- Optimistic concurrency
- Multi-tenancy
- Bulk and patch compatibility
- Batched and compiled queries
- Change tracking does not contain plaintext
- Logs do not contain plaintext, keys, ciphertext, or wrapped keys

### 21.5 Vault tests

- JWT issuer/audience/signature/expiry validation
- mTLS principal mapping where enabled
- Tenant/path/action policy matrix
- Default deny and deny precedence
- Audit failure prevents successful disclosure
- Mutation and audit atomicity
- Concurrent version creation and retry
- Rewrap and recovery exercises
- Provider outage and sealed-state behavior
- Authenticated SurrealDB server configuration

The current development `docker-compose.yml` uses `--unauthenticated`; Vault
security integration tests require a separate authenticated configuration.

---

## 22. Open Decisions

The following still require explicit acceptance before their implementation:

1. Is `AeroDB.Sable.Encryption` a separate package, or should the first
   implementation remain in `AeroDB.Sable` behind optional registration?
2. Should the local mounted-file provider be production-supported for
   single-node deployments or explicitly development-only?

Resolved for the current delivery:

- Field encryption v1 supports `string` and `byte[]`.
- Whole-document `.Encrypt()` remains a Phase D TODO but is deferred until
  after the initial Vault delivery. It requires source-generated payload
  codecs and its own persistence, query, operational-metadata, and
  memory-pressure conformance surface.
5. Which external identity provider/workload identity environment is the first
   Vault deployment target?
6. Is mTLS an optional alternative to access tokens or an additional
   requirement for selected workloads?
7. Which external append-only audit destination should be supported first?
8. Does any near-term tenant require cryptographic erasure or customer-managed
   keys? If yes, revisit the deferred tenant intermediate-key layer.

---

## 23. References

### SurrealDB

- [Crypto database functions](https://surrealdb.com/docs/reference/query-language/functions/database-functions/crypto)
- [Security best practices](https://surrealdb.com/docs/learn/security/best-practices/security-best-practices)
- [Transactions and isolation](https://surrealdb.com/docs/transactions-and-isolation)
- [DEFINE TABLE and permissions](https://surrealdb.com/docs/reference/query-language/statements/define/table)
- [ACCESS statement](https://surrealdb.com/docs/reference/query-language/statements/access)

### .NET

- [AES-GCM encryption API](https://learn.microsoft.com/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0)
- [ChaCha20-Poly1305 API](https://learn.microsoft.com/dotnet/api/system.security.cryptography.chacha20poly1305?view=net-10.0)
- [Cross-platform cryptography](https://learn.microsoft.com/dotnet/standard/security/cross-platform-cryptography)
- [Microsoft SDL cryptographic recommendations](https://learn.microsoft.com/security/engineering/cryptographic-recommendations)
- [C# expression trees](https://learn.microsoft.com/dotnet/csharp/advanced-topics/expression-trees/)
- [C# source generators](https://learn.microsoft.com/dotnet/csharp/roslyn-sdk/#source-generators)
- [ASP.NET Core Identity `PersonalDataAttribute`](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.identity.personaldataattribute?view=aspnetcore-10.0)
- [ASP.NET Core Identity `ProtectedPersonalDataAttribute`](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.identity.protectedpersonaldataattribute?view=aspnetcore-10.0)
- [JWT bearer authentication in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)
- [Certificate authentication in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/authentication/certauth?view=aspnetcore-10.0)

### Cryptographic standards

- [RFC 8439: ChaCha20 and Poly1305 for IETF Protocols](https://www.rfc-editor.org/rfc/rfc8439)
