# AeroDB Event Sourcing & Change Tracking — Design Spec

> **Status:** Draft  
> **Last updated:** 2026-07-08  
> **Author:** Agent review (Oracle + Architect)  
> **Applies to:** AeroDB (SurrealDB-backed document + event store)

---

## Table of Contents

1. [Overview](#1-overview)
2. [Four-Layer Architecture](#2-four-layer-architecture)
3. [Layer 1: `ChangeTracking()` — Automatic Technical Audit Trail](#3-layer-1-changetracking--automatic-technical-audit-trail)
4. [Layer 2: `EventStream<T>()` — Explicit Domain Event Streams](#4-layer-2-eventstreamt--explicit-domain-event-streams)
5. [Layer 3: `Patch<T>()` + `PatchEvents()` — Semantic Command Boundary](#5-layer-3-patcht--patchevents--semantic-command-boundary)
6. [Layer 4: `Projection<T>()` — Source-Generated Projection Infrastructure](#6-layer-4-projectiont--source-generated-projection-infrastructure)
7. [Command Model: Three Levels of Sophistication](#7-command-model-three-levels-of-sophistication)
8. [Internal Pipeline Layers](#8-internal-pipeline-layers)
9. [Source Generator Design](#9-source-generator-design)
10. [`IConfigureAeroDB` — Modular Configuration](#10-iconfigureaerodb--modular-configuration)
11. [Checkpointing Model](#11-checkpointing-model)
12. [SurrealDB Transaction Model](#12-surrealdb-transaction-model)
13. [Migration Path & Coexistence](#13-migration-path--coexistence)
14. [Implementation Phases](#14-implementation-phases)
15. [Validation Checkpoints & Open Risks](#15-validation-checkpoints--open-risks)
16. [File/Namespace Layout](#16-filenamespace-layout)

---

## 1. Overview

AeroDB already implements **Marten-style event sourcing** as an application-level pattern:

- A dedicated `mt_events` table stores domain events as rows
- `EventStore` manages append, versioning, and optimistic concurrency
- Projections run via an in-process async daemon that polls `mt_events`
- Events are business-level domain events (`OrderShipped`, `MoneyDeposited`)

This spec extends AeroDB with **four new layers** that build on SurrealDB's native primitives while preserving full backward compatibility with the existing event sourcing infrastructure.

### Key Design Decisions

1. **`Patch<T>()` is the semantic command boundary** — domain events are captured at the command boundary via typed patch operations (`Increment`, `Decrement`, `Replace`, `Adjust`), not inferred from passive before/after database diffs. This preserves business intent.

2. **Technical events ≠ domain events** — `ChangeTracking()` generates automatic database-level audit events (`AccountCreated`, `AccountUpdated`, `AccountDeleted` with before/after snapshots). `EventStream<T>()` stores explicit domain events (`MoneyDeposited`, `OrderShipped`). Separate tables, separate concerns.

3. **Convention-based source gen** — projection `Apply(EventType e)` methods are discovered by convention at compile time. The source generator produces dispatchers, workers, and checkpoint infrastructure. Runtime fluent config (`Schema.Projection<T>().Handles<>()`) configures infrastructure, not dispatch logic.

4. **Evolution, not revolution** — all new patterns coexist with existing `IEvents`, `EventStore`, `InlineProjection`, `AsyncDaemon`. No breaking changes. Adoption is per-aggregate, per-projection.

---

## 2. Four-Layer Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    IConfigureAeroDB                          │
│         (assembly scanning, modular config)                  │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌──────────────────┐  ┌──────────────────┐                 │
│  │  ChangeTracking  │  │  EventStream<T>  │                 │
│  │  (audit trail)    │  │  (domain events)  │                 │
│  │                   │  │                   │                 │
│  │  aero_audit_log   │  │  account_events   │                 │
│  │  DEFINE EVENT     │  │  UNIQUE INDEX     │                 │
│  │  CHANGEFEED       │  │  append API       │                 │
│  └──────────────────┘  └────────┬──────────┘                 │
│                                 │                            │
│  ┌──────────────────┐  ┌────────▼──────────┐                 │
│  │  Patch<T>        │  │  PatchEvents()    │                 │
│  │  (semantic ops)   │◄─┤  (event rules)    │                 │
│  │                   │  │                   │                 │
│  │  Increment        │  │  OnIncrement →    │                 │
│  │  Decrement        │  │  MoneyDeposited   │                 │
│  │  Replace          │  │  OnReplace →      │                 │
│  │  Adjust           │  │  BalanceCorrected │                 │
│  └────────┬──────────┘  └──────────────────┘                 │
│           │                                                   │
│  ┌────────▼──────────────────────────────┐                    │
│  │  Projection<T> (source gen)          │                    │
│  │                                       │                    │
│  │  Apply(EventType) → dispatcher       │                    │
│  │  checkpoint tracking                  │                    │
│  │  async daemon integration             │                    │
│  └───────────────────────────────────────┘                    │
│                                                              │
├─────────────────────────────────────────────────────────────┤
│              SurrealDB Primitives                             │
│  DEFINE EVENT │ CHANGEFEED │ TRANSACTIONS │ UNIQUE INDEX     │
└─────────────────────────────────────────────────────────────┘
```

### Layer Responsibilities

| Layer | Concern | Mechanism | Consumer |
|-------|---------|-----------|----------|
| `ChangeTracking()` | Audit/compliance | `DEFINE EVENT` + `CHANGEFEED` | Audit UIs, compliance |
| `EventStream<T>()` | Domain event storage | Typed append API + `mt_events` (or custom table) | Projections, subscriptions |
| `Patch<T>()` + `PatchEvents()` | Semantic command boundary | Expression tree rules + transactional pipeline | Application code |
| `Projection<T>()` | Read-side builds | Source-gen dispatch + `AsyncDaemon` | Query models, views |

---

## 3. Layer 1: `ChangeTracking()` — Automatic Technical Audit Trail

### Purpose

Generate SurrealDB-native infrastructure that automatically records every create, update, and delete on a document table for audit, compliance, and change replay purposes.

### API

```csharp
// Minimal — sensible defaults
Schema.For<Account>()
    .ChangeTracking();

// Full configuration
Schema.For<Account>()
    .ChangeTracking(x => x
        .ChangeFeed(retention: "30d", includeOriginal: true)
        .AuditTrail()
        .EventLog()
        .TrackCreates()
        .TrackUpdates()
        .TrackDeletes()
        .Ignore(a => a.UpdatedAt));
```

### Default Behavior (no config)

When `.ChangeTracking()` is called with no arguments, AeroDB generates:

1. **`DEFINE TABLE account SCHEMAFULL CHANGEFEED 30d INCLUDE ORIGINAL`** — enables change replay via `SHOW CHANGES`
2. **`DEFINE TABLE aero_audit_log`** — a shared audit log table (created once, not per model)
3. **`DEFINE EVENT account_audit_created_event`** (sync, same transaction) — fires on `CREATE`
4. **`DEFINE EVENT account_audit_updated_event`** (sync, same transaction) — fires on `UPDATE`
5. **`DEFINE EVENT account_audit_deleted_event`** (sync, same transaction) — fires on `DELETE`

### Generated SurrealQL (Default)

```sql
-- Document table with changefeed
DEFINE TABLE account SCHEMAFULL CHANGEFEED 30d INCLUDE ORIGINAL;

DEFINE FIELD owner ON TABLE account TYPE string;
DEFINE FIELD balance ON TABLE account TYPE decimal;
DEFINE FIELD created_at ON TABLE account TYPE datetime DEFAULT time::now();
DEFINE FIELD updated_at ON TABLE account TYPE option<datetime>;

-- Shared audit log table (created once per database)
DEFINE TABLE aero_audit_log SCHEMAFULL;

DEFINE FIELD source_table ON TABLE aero_audit_log TYPE string;
DEFINE FIELD source_record ON TABLE aero_audit_log TYPE record;
DEFINE FIELD operation ON TABLE aero_audit_log TYPE string;
DEFINE FIELD before ON TABLE aero_audit_log TYPE option<object>;
DEFINE FIELD after ON TABLE aero_audit_log TYPE option<object>;
DEFINE FIELD changed_at ON TABLE aero_audit_log TYPE datetime DEFAULT time::now();
DEFINE FIELD changed_by ON TABLE aero_audit_log TYPE option<record>;

-- Audit trigger (created for each tracked table)
DEFINE EVENT OVERWRITE account_audit_created_event
ON TABLE account
WHEN $event = "CREATE"
THEN (
    CREATE aero_audit_log SET
        source_table = "account",
        source_record = $value.id,
        operation = $event,
        before = NONE,
        after = $value,
        changed_at = time::now(),
        changed_by = $auth.id
);

DEFINE EVENT OVERWRITE account_audit_updated_event
ON TABLE account
WHEN $event = "UPDATE"
THEN (
    CREATE aero_audit_log SET
        source_table = "account",
        source_record = $value.id,
        operation = $event,
        before = $before,
        after = $after,
        changed_at = time::now(),
        changed_by = $auth.id
);

DEFINE EVENT OVERWRITE account_audit_deleted_event
ON TABLE account
WHEN $event = "DELETE"
THEN (
    CREATE aero_audit_log SET
        source_table = "account",
        source_record = $value.id,
        operation = $event,
        before = $before,
        after = NONE,
        changed_at = time::now(),
        changed_by = $auth.id
);
```

### `ChangeTrackingOptions` Class

```csharp
public sealed class ChangeTrackingOptions
{
    public bool EnableChangeFeed { get; set; } = true;
    public string ChangeFeedRetention { get; set; } = "30d";
    public bool IncludeOriginal { get; set; } = true;

    public bool EnableAuditTrail { get; set; } = true;
    public bool EnableEventLog { get; set; } = false;

    public bool TrackCreates { get; set; } = true;
    public bool TrackUpdates { get; set; } = true;
    public bool TrackDeletes { get; set; } = true;

    public bool UseAsyncEvents { get; set; } = true;

    public IReadOnlyList<string> IgnoredFields { get; init; } = [];
}
```

### What It Does NOT Replace

`ChangeTracking()` generates **technical events**: `AccountCreated`, `AccountUpdated`, `AccountDeleted` with before/after snapshots. These are database-level events that capture *what* changed, not *why*.

It does **not** generate domain events like `MoneyDeposited` or `OrderShipped`. Those require the `PatchEvents()` / `EventStream<T>()` layers.

---

## 4. Layer 2: `EventStream<T>()` — Explicit Domain Event Streams

### Purpose

Define typed domain event streams with append-only semantics, stream versioning, and optimistic concurrency. This is the foundation for real event sourcing.

### API

```csharp
Schema.EventStream<Account>()
    .UseTable<AccountEvent>()
    .Events<AccountOpened, MoneyDeposited, MoneyWithdrawn>()
    .StreamId(x => x.StreamId)
    .Version(x => x.Version)
    .EventType(x => x.EventType)
    .Payload(x => x.Payload)
    .Serialization(EventSerializationMode.Json);
```

### Generated SurrealQL

```sql
DEFINE TABLE account_event SCHEMAFULL CHANGEFEED 30d;

DEFINE FIELD stream_id ON TABLE account_event TYPE string;
DEFINE FIELD aggregate ON TABLE account_event TYPE record<account>;
DEFINE FIELD version ON TABLE account_event TYPE int;
DEFINE FIELD event_type ON TABLE account_event TYPE string;
DEFINE FIELD payload ON TABLE account_event TYPE object;
DEFINE FIELD metadata ON TABLE account_event TYPE object DEFAULT {};
DEFINE FIELD occurred_at ON TABLE account_event TYPE datetime DEFAULT time::now();

DEFINE INDEX account_event_stream_version_unique
ON TABLE account_event
FIELDS stream_id, version UNIQUE;
```

### Multi-Tenancy via Namespace/Database

AeroDB does **not** use a `tenantId` column for multi-tenancy. Instead, tenant isolation is handled structurally via SurrealDB's **namespace + database** scoping:

```
namespace: tenant_acme
  ├── database: accounting  → account_event table
  └── database: analytics   → account_event table

namespace: tenant_megacorp
  └── database: accounting  → account_event table (isolated)
```

Each tenant's events live in a completely separate database scope. This removes an entire class of "forgot to filter by tenant" bugs — queries against the wrong namespace simply return zero results rather than leaking data.

The `EventStream<T>` configuration does **not** include a `.Tenant(x => x.Tenant)` mapping because the tenant is resolved from the connection (`ISurrealDbSession` scoped to namespace/database), not from event data.

> **Cross-tenant admin/reporting queries** require a separate multiplexer service (outside AeroDB) that fans out per-tenant queries with bounded concurrency, using a `ITenantConnectionFactory` → scoped `SurrealDbClient` per tenant.

### Append API

```csharp
// Typed append
await session.Events
    .For<Account>(accountId)
    .AppendAsync(new MoneyDeposited(accountId, 50m));

// With expected version (optimistic concurrency)
await session.Events
    .For<Account>(accountId)
    .AppendAsync(new MoneyDeposited(accountId, 50m), expectedVersion: 3);

// Streaming read
await foreach (var envelope in session.Events
    .ReadFromAsync<AccountEvent>(checkpoint, ct))
{
    var domainEvent = Deserialize(envelope);
    // process
}
```

### Usage (Append via Patch, the Preferred Path)

Most users will interact with event streams through the `Patch<T>()` layer (Layer 3), which appends events transactionally as part of a state mutation. Direct `session.Events.For<T>().AppendAsync()` is the low-level API for advanced scenarios.

---

## 5. Layer 3: `Patch<T>()` + `PatchEvents()` — Semantic Command Boundary

### Purpose

This is the **architectural keystone** of the design. Domain events are captured at the command boundary — when a developer calls a typed `Patch<T>()` operation — not inferred from passive database before/after diffs.

The key insight: `Increment(x.Balance, 50).WithReason("customer_deposit")` captures *why* the change happened. A passive diff (`before.Balance = 100, after.Balance = 150`) cannot distinguish `MoneyDeposited` from `AdminAdjustment` from `BalanceCorrection`.

### Usage

```csharp
// Application code
await session.Patch<Account>(accountId)
    .Increment(x => x.Balance, 50m)
    .WithReason("customer_deposit")
    .SaveChangesAsync();
```

### Configuration (in IConfigureAeroDB)

```csharp
public sealed class AccountingAeroConfig : IConfigureAeroDB
{
    public void Configure(IAeroSchemaBuilder schema)
    {
        schema.For<Account>()
            .PatchEvents(events =>
            {
                events.OnIncrement(x => x.Balance)
                    .Emit((account, amount, ctx) =>
                        new MoneyDeposited(
                            AccountId: account.Id,
                            Amount: amount,
                            UserId: ctx.UserId));

                events.OnDecrement(x => x.Balance)
                    .Emit((account, amount, ctx) =>
                        new MoneyWithdrawn(
                            AccountId: account.Id,
                            Amount: amount,
                            UserId: ctx.UserId));

                events.OnReplace(x => x.Balance)
                    .When((before, after, ctx) => ctx.Reason == "Correction")
                    .Emit((before, after, ctx) =>
                        new BalanceCorrected(
                            AccountId: after.Id,
                            PreviousBalance: before.Balance,
                            NewBalance: after.Balance,
                            UserId: ctx.UserId));

                events.OnReplace(x => x.Balance)
                    .When((before, after, ctx) => ctx.Reason == "AdminAdjustment")
                    .Emit((before, after, ctx) =>
                        new AdminAdjustment(
                            AccountId: after.Id,
                            PreviousBalance: before.Balance,
                            NewBalance: after.Balance,
                            Reason: ctx.Metadata["reason"],
                            UserId: ctx.UserId));
            });
    }
}
```

### Patch Operations

| Operation | Example | Description |
|-----------|---------|-------------|
| `Increment` | `.Increment(x => x.Balance, 50m)` | Add to a numeric field |
| `Decrement` | `.Decrement(x => x.Balance, 25m)` | Subtract from a numeric field |
| `Replace` | `.Replace(x => x.Status, "Closed")` | Set a field to a new value |
| `Adjust` | `.Adjust(x => x.Balance, -25m, "FeeApplied")` | Adjust with named reason |

### Patch Context (`PatchContext`)

```csharp
public sealed class PatchContext
{
    public string? UserId { get; init; }
    public string? Reason { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
    public DateTimeOffset Timestamp { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
```

### Transactional Pipeline

When `SaveChangesAsync()` is called, AeroDB executes a single SurrealDB transaction:

```
BEGIN TRANSACTION

  1. Load current document (SELECT * FROM account WHERE id = $id)
  2. Validate expected version (if optimistic concurrency)
  3. Resolve patch operation type and field
  4. Match against configured PatchEvents rules
  5. If rule matches, create domain event instance
  6. Compute stream version (SELECT max(version) FROM events WHERE stream_id = $id)
  7. Append event to event stream (CREATE account_events CONTENT {...})
  8. Apply database patch (UPDATE account SET balance += 50)
  9. If ChangeTracking() is active, DEFINE EVENT triggers fire (same TX)

COMMIT TRANSACTION
```

If no `PatchEvents` rule matches the operation, the patch falls back to **state-only mutation** — the document is updated but no domain event is appended.

### Rules for Rule Matching

1. Operation kind (`Increment`, `Decrement`, `Replace`, `Adjust`) must match
2. Expression target field must match (`x => x.Balance`)
3. If `.When()` predicate is defined, it must evaluate to `true`
4. First match wins (ordered by specificity: most `.When()` conditions first)
5. If no match: state-only mutation (no error)

### `PatchEventRule` Model (Internal)

```csharp
public sealed class PatchEventRule
{
    public Type EntityType { get; init; } = default!;
    public string FieldName { get; init; } = "";
    public PatchOperationKind OperationKind { get; init; }
    public Type EventType { get; init; } = default!;
    public Func<object, object, PatchContext, object> Predicate { get; init; } = default!;
    public Func<object, object, PatchContext, object> EventFactory { get; init; } = default!;
}
```

The composite key for event rule resolution: **(EntityType + FieldName + OperationKind + Predicate) → EventType**

---

## 6. Layer 4: `Projection<T>()` — Source-Generated Projection Infrastructure

### Purpose

Provide compile-time-generated projection dispatchers, checkpoint workers, and integration with the existing `AsyncDaemon`. The user writes business logic in `Apply(EventType e)` methods. AeroDB's source generator produces the plumbing.

### API

```csharp
Schema.Projection<AccountBalanceProjection>()
    .FromStream<AccountEvent>()
    .Handles<AccountOpened>()
    .Handles<MoneyDeposited>()
    .Handles<MoneyWithdrawn>()
    .Handles<BalanceCorrected>()
    .Checkpointed()
    .Async();
```

### User-Written Business Logic

```csharp
public partial class AccountBalanceProjection
{
    public RecordId Id { get; set; }
    public decimal Balance { get; set; }

    public void Apply(AccountOpened e)
    {
        Id = e.AccountId;
        Balance = e.OpeningBalance;
    }

    public void Apply(MoneyDeposited e)
    {
        Balance += e.Amount;
    }

    public void Apply(MoneyWithdrawn e)
    {
        Balance -= e.Amount;
    }

    public void Apply(BalanceCorrected e)
    {
        Balance = e.NewBalance;
    }
}
```

### Source-Generated Dispatcher

```csharp
// Generated by AeroDB.Projections.SourceGenerator
internal static class AccountBalanceProjectionDispatcher
{
    public static void Apply(AccountBalanceProjection projection, object @event)
    {
        switch (@event)
        {
            case AccountOpened e:
                projection.Apply(e);
                break;
            case MoneyDeposited e:
                projection.Apply(e);
                break;
            case MoneyWithdrawn e:
                projection.Apply(e);
                break;
            case BalanceCorrected e:
                projection.Apply(e);
                break;
            default:
                throw new NotSupportedException(
                    $"Event type '{@event.GetType().Name}' is not handled by AccountBalanceProjection.");
        }
    }
}
```

### Source-Generated Worker (Async)

```csharp
// Generated by AeroDB.Projections.SourceGenerator
internal sealed class AccountBalanceProjectionWorker
{
    private readonly IAeroDbSession _session;

    public async Task RunAsync(CancellationToken ct)
    {
        var checkpoint = await _session.Projections
            .GetCheckpointAsync("AccountBalanceProjection", ct);

        await foreach (var envelope in _session.Events
            .ReadFromAsync<AccountEvent>(checkpoint, ct))
        {
            var projection = await _session.Projections
                .LoadAsync<AccountBalanceProjection>(envelope.StreamId, ct)
                ?? new AccountBalanceProjection();

            var @event = Deserialize(envelope);
            AccountBalanceProjectionDispatcher.Apply(projection, @event);

            await _session.StoreAsync(projection, ct);
            await _session.Projections
                .SaveCheckpointAsync("AccountBalanceProjection", envelope.Position, ct);
        }
    }
}
```

### Compile-Time Diagnostics

The source generator emits warnings (not errors) when:

- A projection class configured with `.Handles<TEvent>()` has no matching `Apply(TEvent e)` method
- A projection class has `Apply(...)` methods but is not marked `partial`
- An `Apply(TEvent e)` method references an event type not registered via `EventStream<T>().Events<>()`

### Convention-Based Discovery

The source generator discovers `Apply(EventType e)` methods by scanning `partial class` candidates for methods named `Apply` that take a single parameter. This means:

- **No attributes required** for basic cases
- **No runtime fluent config needed** for dispatch — the generator sees the types at compile time
- The `Schema.Projection<T>().Handles<TEvent>()` API configures **infrastructure** (checkpointing, async), not dispatch logic

### Integration with Existing Projection Infrastructure

The generated projection registers as an `IProjection` instance and is processed by the existing `AsyncDaemon` / `ProjectionShard` system. The existing `InlineProjection`, `EventProjection`, and `SnapshotProjection` classes continue to work unchanged.

| Existing | New | Relationship |
|----------|-----|------------|
| `InlineProjection<T>` | `Projection<T>()` source gen | New registers as `IProjection`, same daemon |
| `EventProjection<T>` | source gen dispatch | Additive — reflection fallback remains |
| `SnapshotProjection<T>` | same pattern | Source gen is optional optimization |
| `AsyncDaemon` | unchanged | Polls both existing and new projections |

---

## 7. Command Model: Three Levels of Sophistication

Different parts of an application need different levels of ceremony. AeroDB supports three levels, all coexisting in the same codebase.

| Level | Pattern | Ceremony | Best For |
|-------|---------|----------|----------|
| **1. Direct Append** | `session.Events.For<T>().AppendAsync(@event, version)` | Low — explicit event, manual version | Pure event sourcing, background jobs, imports |
| **2. Patch + Event Mapping** | `session.Patch<T>().Increment(x.Balance, 50m).WithReason("Deposit").SaveChangesAsync()` | High DX — patch drives event | Normal app development, CRUD with audit |
| **3. Aggregate Methods** | `account.Deposit(50m)` → `session.Events.For<T>().AppendAsync(account.PendingEvents, version)` | Highest encapsulation | Rich domain models, DDD aggregates |

### Level 1: Direct Explicit Event Append

Best for pure event sourcing scenarios where the developer wants full control over the event stream.

```csharp
await session.Events
    .For<Account>(accountId)
    .AppendAsync(new MoneyDeposited(accountId, 50m), expectedVersion: 3);
```

No state mutation — the event is appended and projections handle the rest.

### Level 2: Patch with Event Mapping

Best DX for normal app developers. The patch operation drives both state mutation and event emission transactionally.

```csharp
await session.Patch<Account>(accountId)
    .Increment(x => x.Balance, 50m)
    .WithReason("CustomerDeposit")
    .SaveChangesAsync();
```

AeroDB resolves the patch operation against configured `PatchEvents` rules, appends the matching domain event, and applies the state change in a single SurrealDB transaction.

### Level 3: Domain Aggregate Methods

Best for rich domain models following DDD patterns.

```csharp
var account = await session.Events
    .AggregateStreamAsync<Account>(accountId);

account.Deposit(50m);

await session.Events
    .For<Account>(accountId)
    .AppendAsync(account.PendingEvents, expectedVersion);
```

The aggregate enforces invariants, raises events internally, and the caller persists them. This maps directly to the existing `LiveStreamAggregation` infrastructure.

### Coexistence

All three levels share the same event stream table and checkpoint tracking. A projection consuming `account_event` processes events regardless of which level produced them. A team can use aggregate methods for core domain logic while using direct append for background job results — all feeding the same projections.

---

## 8. Internal Pipeline Layers

AeroDB's implementation is organized into five internal layers between the public API and SurrealDB:

```
┌─────────────────────────────────────────────────────┐
│                  AeroDB Public API                   │
│  Schema.For<T>()  │  Patch<T>()  │  Projection<T>()  │
└──────────────────────┬──────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────┐
│               AeroDB Metadata Model                  │
│                                                      │
│  EntityMapping        ChangeTrackingMapping          │
│  EventStreamMapping   PatchEventMapping              │
│  ProjectionMapping    RelationshipMapping            │
└──────────────────────┬──────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────┐
│               Generated Code (Source Gen)             │
│                                                      │
│  dispatchers          serializers                     │
│  projection runners   checkpoint glue                 │
│  diagnostics          registration                    │
└──────────────────────┬──────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────┐
│               SurrealDB Provider                     │
│                                                      │
│  DDL generator       transaction builder             │
│  patch translator    event append translator          │
│  changefeed reader   live query reader                │
└──────────────────────┬──────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────┐
│               Runtime Workers                        │
│                                                      │
│  async projection worker   checkpoint manager        │
│  outbox processor           replay/rebuild service    │
└─────────────────────────────────────────────────────┘
```

### Layer Responsibilities

| Layer | Role |
|-------|------|
| **Public API** | Fluent surface developers interact with (`Schema.For<T>()`, `Patch<T>()`, `Projection<T>()`) |
| **Metadata Model** | Internal POCOs that capture all configuration (entity mappings, event stream definitions, patch rules, projection configs) — populated at startup via `IConfigureAeroDB` |
| **Generated Code** | Source-gen output: dispatchers, serializers, projection runners, compile-time diagnostics |
| **SurrealDB Provider** | Translates metadata + operations into SurrealQL DDL, transactions, queries |
| **Runtime Workers** | Long-running services: async daemon projection runner, checkpoint manager, outbox processor, rebuild coordinator |

---

## 9. Source Generator Design

### Files in `src/AeroDB.SourceGenerators/`

| Generator | Purpose |
|-----------|---------|
| `ProjectionDispatchGenerator.cs` | Discovers `Apply(EventType e)` methods by convention, generates `switch` dispatcher |
| `ProjectionRegistrationGenerator.cs` | Generates DI registration for discovered projections |
| `EventTypeMetadataGenerator.cs` | Generates event type name constants for string-free dispatch |
| `AeroDBConfiguratorGenerator.cs` | Existing — discovers `IConfigureAeroDB` implementors |

### Discovery Algorithm

The `ProjectionDispatchGenerator`:

1. Scans all types in the compilation for `partial class` declarations
2. For each partial class, looks for methods named `Apply` with a single parameter
3. Collects the parameter type as the handled event type
4. Generates a `switch` expression dispatcher partial class
5. Generates diagnostics for missing handlers

### Bridge Between Runtime and Compile-Time

The source generator **cannot** analyze the content of `IConfigureAeroDB.Configure()` delegate bodies (they are runtime). This design works around that by using **convention-based discovery**:

- **Compile-time**: Generator finds `Apply(MoneyDeposited e)` → generates dispatcher switch case
- **Runtime**: `Schema.Projection<T>().Handles<MoneyDeposited>()` configures checkpointing and async behavior
- **Validation**: If a `.Handles<TEvent>()` is declared at runtime but no `Apply(TEvent e)` method exists, a runtime warning is logged (with reflection fallback)

---

## 10. `IConfigureAeroDB` — Modular Configuration

### Purpose

Keep business event mapping and projection configuration out of `Program.cs` by allowing feature modules to self-register their AeroDB configuration.

### Interface

```csharp
public interface IConfigureAeroDB
{
    void Configure(IAeroSchemaBuilder schema);
}
```

### `IAeroSchemaBuilder` Facade

```csharp
public interface IAeroSchemaBuilder
{
    // Document mapping
    DocumentMapping<T> For<T>() where T : class;

    // Change tracking
    DocumentMapping<T> For<T>(Action<DocumentMapping<T>> configure) where T : class;

    // Event streams
    IEventStreamConfiguration EventStream<T>() where T : class;

    // Projections
    IProjectionConfiguration Projection<TProjection>() where TProjection : class;

    // Global defaults
    Policies Policies { get; }
}
```

### Example Feature Module

```csharp
public sealed class BankingAeroConfig : IConfigureAeroDB
{
    public void Configure(IAeroSchemaBuilder schema)
    {
        // 1. Document mapping with change tracking
        schema.For<Account>(doc =>
        {
            doc.ChangeTracking(x => x
                .AuditTrail()
                .ChangeFeed("30d")
                .Ignore(a => a.UpdatedAt));

            doc.PatchEvents(events =>
            {
                events.OnIncrement(x => x.Balance)
                    .Emit((acct, amt, ctx) =>
                        new MoneyDeposited(acct.Id, amt));

                events.OnDecrement(x => x.Balance)
                    .Emit((acct, amt, ctx) =>
                        new MoneyWithdrawn(acct.Id, amt));

                events.OnReplace(x => x.Balance)
                    .When((b, a, ctx) => ctx.Reason == "Correction")
                    .Emit((b, a, ctx) =>
                        new BalanceCorrected(a.Id, b.Balance, a.Balance));
            });
        });

        // 2. Event stream
        schema.EventStream<Account>()
            .UseTable<AccountEvent>()
            .Events<AccountOpened, MoneyDeposited, MoneyWithdrawn, BalanceCorrected>()
            .StreamId(x => x.StreamId)
            .Version(x => x.Version);

        // 3. Projection
        schema.Projection<AccountBalanceProjection>()
            .FromStream<AccountEvent>()
            .Handles<AccountOpened>()
            .Handles<MoneyDeposited>()
            .Handles<MoneyWithdrawn>()
            .Handles<BalanceCorrected>()
            .Checkpointed()
            .Async();
    }
}
```

### Registration

```csharp
// Program.cs
builder.Services.AddAeroDb(options =>
{
    options.UseSurrealDb(connectionString);
    options.ApplyConfigurationsFromAssembly(typeof(BankingAeroConfig).Assembly);
});
```

---

## 11. Checkpointing Model

### Current State

The existing `mt_projection_progress` table tracks per-projection global sequence numbers:

```sql
DEFINE TABLE mt_projection_progress SCHEMALESS;
DEFINE FIELD projection_name ... TYPE string;
DEFINE FIELD last_version ... TYPE int;
DEFINE FIELD last_updated ... TYPE datetime;
DEFINE INDEX idx_projection_progress_name ON ... projection_name UNIQUE;
```

### Target State

The new design needs per-projection, per-tenant, per-stream checkpoints:

```sql
DEFINE TABLE aero_projection_checkpoint SCHEMAFULL;

DEFINE FIELD projection_name ON aero_projection_checkpoint TYPE string;
DEFINE FIELD tenant_id ON aero_projection_checkpoint TYPE option<string>;
DEFINE FIELD stream_id ON aero_projection_checkpoint TYPE option<string>;
DEFINE FIELD last_version ON aero_projection_checkpoint TYPE int;
DEFINE FIELD last_sequence ON aero_projection_checkpoint TYPE int;
DEFINE FIELD last_updated ON aero_projection_checkpoint TYPE datetime;

DEFINE INDEX idx_checkpoint_unique
ON TABLE aero_projection_checkpoint
COLUMNS projection_name, tenant_id, stream_id UNIQUE;
```

### Migration

1. Add new fields to existing `mt_projection_progress` (SCHEMALESS — additive, non-breaking)
2. Existing projections continue with null tenant/stream fields
3. New source-gen projections populate all fields
4. `AsyncDaemon` shards optionally track per-stream watermarks when granular checkpoints are present

---

## 12. SurrealDB Transaction Model

### Isolation Level

SurrealDB uses **snapshot isolation** (not serializable). This means:

- Two concurrent transactions can both read `max(version) = 5` and both compute `version = 6`
- The `UNIQUE` index on `(stream_id, version)` catches the duplicate at COMMIT time
- One transaction succeeds, the other throws a constraint violation

This is **optimistic concurrency** — the standard pattern for event stores.

### Transactional Pipeline Validation

The existing `EventStore.Append(streamId, expectedVersion, ...)` pattern is already correct:

```
BEGIN TRANSACTION
  SELECT max(version) FROM events WHERE stream_id = @id   // snapshot read
  IF currentVersion != expectedVersion → throw
  CREATE events CONTENT {stream_id, version: currentVersion+1, ...}
  UPDATE doc SET ...                                      // same TX
COMMIT TRANSACTION
```

If another transaction committed concurrently, the `UNIQUE` index fires at COMMIT. Both the event insert and document update are rolled back atomically.

### Retry Policy

Add a Polly-based retry policy for `ConcurrencyException` at the session level:

```csharp
options.AddConcurrencyRetryPolicy(retryCount: 3, baseDelay: TimeSpan.FromMilliseconds(50));
```

---

## 13. Migration Path & Coexistence

### Guiding Principle

The new layers are **additive**. Every existing API, class, and pattern continues to work. Users adopt new features per-aggregate, per-projection, at their own pace.

### Adoption Scenarios

| If you have this... | You can add this... | Without touching... |
|---------------------|---------------------|---------------------|
| `Schema.For<Account>()` | `.ChangeTracking()` | Any existing event sourcing |
| `IEvents.Append(streamId, events)` | `session.Patch<T>().Increment().SaveChangesAsync()` | Existing event streams |
| `SnapshotProjection<Account>` with `Apply(...)` | `Schema.Projection<Account>().FromStream<>()` | Your `Apply(...)` methods |
| Custom projection | `.Handles<TEvent>()` | Any existing daemon setup |

### Compatibility Matrix

| Existing Feature | Coexistence with New Layers |
|-----------------|----------------------------|
| `IEvents` | New `EventStream<T>` wraps `IEvents` internally. Old API remains. |
| `mt_events` | Same table, or new per-stream tables. Configurable. |
| `InlineProjection<T>` | Processes alongside new source-gen projections in the same daemon. |
| `EventProjection<T>` | Source gen is optional optimization over reflection dispatch. |
| `AsyncDaemon` / `ProjectionShard` | Unchanged. New projections register as `IProjection` instances. |
| `EventTriggerManager` | `ChangeTracking()` uses the same infrastructure. |
| `EventSourcingOptions` | Extended with new config sections, no breaking changes. |

---

## 14. Implementation Phases

> **Pre-Phase 1 gate:** Validate optimistic concurrency + transaction atomicity (Checkpoint 1) against a real SurrealDB instance before the append API is considered final. The `Patch<T>` pipeline and the entire event store API depend on this guarantee.

### Phase 0 — Foundation (P0)

| Item | Description | Dependencies |
|------|-------------|-------------|
| `IAeroSchemaBuilder` | Fluent builder facade over `StoreOptions` | None |
| `IConfigureAeroDB.Configure(IAeroSchemaBuilder)` | New overload for schema config | `IAeroSchemaBuilder` |
| `ApplyConfigurationsFromAssembly()` | Assembly scanning for `IConfigureAeroDB` | `IConfigureAeroDB` |
| `DocumentMapping<T>.ChangeTracking()` | Entry point on existing document mapping | `IAeroSchemaBuilder` |

### Phase 0 — EventStream<T> (P0)

| Item | Description | Dependencies |
|------|-------------|-------------|
| `EventStreamConfiguration` | Fluent config for stream ID, version, event types | Foundation |
| `EventStreamGenerator` | DDL generation for event table + unique index | Foundation |
| `EventStreamAppender` | Typed append API with version checks | Foundation |
| `ReadFromAsync<T>()` | Streaming event reader for projections | Foundation |

### Phase 1 — ChangeTracking (P1)

| Item | Description | Dependencies |
|------|-------------|-------------|
| `ChangeTrackingOptions` | Configuration POCO | Foundation |
| `ChangeTrackingGenerator` | Generates `DEFINE EVENT` + `CHANGEFEED` DDL | `EventTriggerManager` |
| Shared `aero_audit_log` table setup | One-time table creation | `SchemaManager` |
| `ChangeTrackingSchemaManager` | Schema diff/update for change tracking | `SchemaDiffer` |

> **Phase 1 must also include a design pass for the async projection worker (Phase 3).** The `PatchEvents` / `EventStream<T>` API shape is constrained by what the projection runner can guarantee (per-stream ordering, checkpoint granularity, poison-event handling, rebuild-while-live). The public API surface should not be finalized until these constraints are understood. See [Checkpoint 2](#checkpoint-2-async-projection-worker-design).

### Phase 1 — Patch<T> + PatchEvents (P1)

| Item | Description | Dependencies |
|------|-------------|-------------|
| `PatchBuilder` | Fluent `.Increment()`, `.Decrement()`, `.Replace()`, `.Adjust()` | Foundation |
| `PatchOperation` | Operation kind + field expression value objects | Foundation |
| `PatchRuleResolver` | Expression tree matching against `PatchEventRule` registry | Foundation |
| `PatchEventRule` | Rule model: op + field + predicate + factory | Foundation |
| `PatchEventsConfiguration` | `.OnIncrement()`, `.OnDecrement()`, `.Emit()` fluent API | Foundation |
| `PatchPipeline` | Transactional pipeline executor | `EventStream<T>`, `PatchRuleResolver` |
| `PatchContext` | User ID, reason, metadata, correlation | Foundation |
| Projection worker design pass | Architecture doc: per-stream ordering, checkpoint granularity, poison events, rebuild-while-live | All Phase 1 items |

### Phase 2 — Projection Source Gen (P2)

| Item | Description | Dependencies |
|------|-------------|-------------|
| `ProjectionDispatchGenerator` | Source gen: discovers `Apply()`, generates dispatcher | Roslyn, `EventStream<T>` |
| `ProjectionRegistrationGenerator` | Source gen: DI registration | `ProjectionDispatchGenerator` |
| `ProjectionConfiguration` | `.FromStream<T>()`, `.Handles<T>()`, `.Checkpointed()`, `.Async()` | Foundation |
| `ISourceGeneratedProjection` | Marker interface | Foundation |

### Phase 2 — Checkpointing Enhancements (P2)

| Item | Description | Dependencies |
|------|-------------|-------------|
| Per-stream checkpoint fields | Add to `mt_projection_progress` | Existing daemon |
| `aero_projection_checkpoint` schema | New rich checkpoint table | Foundation |
| Shard-level watermark tracking | Optional per-stream granularity | `AsyncDaemon` |

### Phase 3 — Declarative Projection Rules (P3)

| Item | Description | Dependencies |
|------|-------------|-------------|
| `.On<AccountOpened>((p, e) => ...)` | Declarative projection rules | Projection source gen |
| Expression body analyzer | Parse `(p, e) => p.Balance = e.Amount` for source-gen | Roslyn |

---

## 15. Validation Checkpoints & Open Risks

### Checkpoint 1 (Highest Priority): Optimistic Concurrency & Transaction Atomicity

**Status:** ⚠️ **Must validate before Phase 1 is complete**

The entire append API design depends on one assumption: SurrealDB enforces a `UNIQUE(stream_id, version)` constraint inside a `BEGIN/COMMIT` transaction such that a violation aborts the **entire** transaction (including any concurrent `UPDATE account SET balance ...`), not just the conflicting `CREATE` statement.

**Test plan — run against a real SurrealDB instance before finalizing the append API:**

1. Open two concurrent sessions
2. Both read `max(version) = 5` from `account_event`
3. Both attempt to append `version = 6` in separate transactions
4. Each transaction also mutates a document table (`UPDATE account SET balance += 50`)
5. Confirm the loser's entire transaction rolls back (both the event insert AND the balance update)
6. Confirm the winner's transaction commits fully

**Additional items to validate:**
- Transaction isolation level at the version-read step — is the `SELECT max(version)` inside the transaction guaranteed to see the latest committed state, or does snapshot isolation cause both readers to see `version = 5`?
- Error code surfaced on unique-index conflict — `AppendAsync` should return a typed result (`VersionConflict | Appended | UnknownFailure`) rather than parsing exception strings, matching the Option/Result/Railway pattern used in the existing codebase

**Fallback if validation fails:** Implement application-level pessimistic locking using a `SELECT ... FOR UPDATE` equivalent (or a dedicated lock table with `DEFINE UNIQUE INDEX`).

### Checkpoint 2: Async Projection Worker Design

**Status:** ⚠️ **Underspecified — design pass needed during Phase 1**

The current plan names the right components (checkpoint table, retry policy, poison-event handling, Task Dataflow pipeline) but the `PatchEvents` / `EventStream` API shape is constrained by what the projection runner can actually guarantee.

**Questions to resolve:**
- Per-stream ordering guarantee? Can the worker guarantee in-order processing within a stream but allow parallel processing across streams?
- Per-tenant / per-database checkpoint granularity? With NS/DB-tenancy, does each database need its own daemon instance?
- Rebuild-while-live semantics? How does a projection rebuild affect the running projection's checkpoint?
- Poison-event handling? Skip-and-log? Dead-letter table? Retry-with-backoff?
- Multi-node coordination (hot/cold leader election) — is this needed for v1?

**Recommendation:** Pull the projection worker design forward from Phase 3 to Phase 1. Produce a design doc covering the transaction shape (event read → apply → checkpoint → store) with the same atomicity analysis applied to the append path. The public API surface of `PatchEvents()` and `EventStream<T>()` depends on these guarantees.

### Checkpoint 3: Source-Gen / Runtime-Config Interaction

**Status:** ✅ **Accepted risk — same pattern as existing Aero CMS cross-project validation**

A source-generated projection dispatcher can be built against an assembly snapshot that predates a runtime `PatchEvents` mapping change.

**Mitigation:**
- Compile-time diagnostics as **warnings** (not errors) for v1
- Runtime validation: if `.Handles<TEvent>()` is declared but no `Apply(TEvent e)` exists, log a warning and fall back to reflection dispatch
- Harder validation (build errors for mismatches) deferred to Phase 4

### Checkpoint 4: Cross-Tenant Admin/Reporting Queries

**Status:** ✅ **Out of AeroDB scope — requires separate multiplexer service**

NS/DB-per-tenant isolation removes the easy `WHERE tenant = X` query path. Admin/reporting queries that span tenants need a separate service.

**Requirements for the multiplexer (not part of AeroDB):**
- Fans out per-tenant queries with bounded concurrency (`TransformBlock`/Task Dataflow), capped to avoid connection storms
- Decides explicitly whether one tenant's failure fails the whole report or is excluded-with-warning
- Assumes uniform schema version across tenants (if schema can drift, the multiplexer needs a capability/version check per tenant)
- Uses a **tenant-keyed connection factory** (`ITenantConnectionFactory` → scoped `SurrealDbClient` per tenant) to avoid session/connection leakage across tenants — a more severe bug class here than in Postgres, since a leak means wrong database/namespace entirely, not just a missing filter

### Q1: What happens when `Patch<T>()` has no configured `PatchEvents` rule?

**Decision:** Fall back to state-only mutation. The document is updated but no domain event is appended. `ChangeTracking()` audit triggers still fire (they are database-level, independent of `Patch<T>()`).

### Q2: How does `Patch<T>()` interact with `ChangeTracking()`?

**Decision:** Two separate mechanisms, complementary. `ChangeTracking()` fires SurrealDB `DEFINE EVENT` triggers on ANY table mutation. `Patch<T>()` appends domain events via the transactional pipeline. They produce different event types in different tables. Both can be active simultaneously with no conflict.

### Q3: Concurrency model for `Patch<T>()`?

**Decision:** Support two modes:
1. **Optimistic** (default where `StoreOptions.UseOptimisticConcurrency` is true) — checks document version before mutating, uses existing `IVersioned` / `VersionAttribute`
2. **Last-write-wins** (default otherwise) — no version check

### Q4: Source gen diagnostics for missing `Apply(...)` — error or warning?

**Decision:** **Warning** (`AERODB001`). Rationale: user may have an intentional placeholder, or use a non-convention method name. Reflection fallback in `EventProjection<T>` handles runtime dispatch. Warning can be suppressed with `#pragma warning disable AERODB001`.

### Q5: Event serialization — reuse existing or new?

**Decision:** **Reuse existing** `EventSerializationMode` (Json/Binary) and `EventSourcingOptions`. The `EventStream<T>` configuration references the same serialization configuration.

### Q6: SurrealDB `ASYNC` events not supported in v2.x?

**Risk:** `DEFINE EVENT ... ASYNC` is a SurrealDB 3.0+ feature. In v2.x, all `DEFINE EVENT` triggers are synchronous (same transaction).

**Mitigation:** Default `UseAsyncEvents = false` for SurrealDB 2.x compatibility. When the server version supports it, users can opt into async via `UseAsyncEvents = true`. AeroDB can detect server version at startup via `INFO FOR KV`.

### Q7: Naming clarity — `ChangeTracking().EventLog()` vs existing `Events` namespace?

**Risk:** The term "event" is overloaded across `IEvent`, `EventStore`, `EventSourcingOptions`, `mt_events`, and the proposed `EventLog()`.

**Mitigation:** Use distinct terminology in XML docs and user-facing docs:
- `ChangeTracking()` = audit / change log / technical events
- `EventStream<T>()` = domain events / business events
- `EventLog()` = outbox records / CDC records

Consider renaming `EventLog()` to `Outbox()` or `Journal()` to reduce confusion.

---

## 16. File/Namespace Layout

### New Files

```
src/AeroDB/
├── Configuration/
│   ├── IAeroSchemaBuilder.cs           # Fluent builder facade
│   └── AeroConfiguratorExtensions.cs   # ApplyConfigurationsFromAssembly
├── ChangeTracking/
│   ├── ChangeTrackingOptions.cs        # Configuration POCO
│   ├── ChangeTrackingGenerator.cs      # DDL generator (DEFINE EVENT + CHANGEFEED)
│   ├── ChangeTrackingSchemaManager.cs  # Schema management
│   ├── AuditLogRecord.cs               # aero_audit_log POCO
│   └── TechnicalEventTypes.cs          # AccountCreated/Updated/Deleted types
├── EventStreams/
│   ├── EventStreamConfiguration.cs     # Fluent config (UseTable, Events, StreamId)
│   ├── EventStreamGenerator.cs         # DDL generator (event table + indexes)
│   ├── EventStreamAppender.cs          # Typed append API
│   └── IEventStreamAppender.cs         # Interface
├── Patching/
│   ├── PatchBuilder.cs                 # Fluent Patch<T> builder
│   ├── PatchOperation.cs               # Increment, Decrement, Replace, Adjust
│   ├── PatchEventsConfiguration.cs     # OnIncrement, OnReplace, Emit
│   ├── PatchRuleResolver.cs            # Expression tree → rule matching
│   ├── PatchPipeline.cs                # Transactional pipeline executor
│   ├── PatchContext.cs                 # Context object (user, reason, metadata)
│   └── PatchEventRule.cs               # Rule model
├── Projections/
│   ├── ProjectionConfiguration.cs      # FromStream, Handles, Checkpointed, Async
│   └── ISourceGeneratedProjection.cs   # Marker interface
└── StoreOptions.cs                     # Extended with new sections
```

### Source Generator

```
src/AeroDB.SourceGenerators/
├── ProjectionDispatchGenerator.cs      # Apply() discovery → switch dispatcher
├── ProjectionRegistrationGenerator.cs  # DI registration
├── EventTypeMetadataGenerator.cs       # Event type name constants
└── AeroDBConfiguratorGenerator.cs      # Existing — discover IConfigureAeroDB
```

### Namespaces

| Namespace | Contents |
|-----------|----------|
| `AeroDB.Configuration` | `IAeroSchemaBuilder`, config extensions |
| `AeroDB.ChangeTracking` | Audit trail, changefeed, technical events |
| `AeroDB.EventStreams` | Typed event stream configuration and append API |
| `AeroDB.Patching` | Semantic patch commands and event rule resolution |
| `AeroDB.Projections` | Existing + `ProjectionConfiguration`, marker interfaces |

---

## Appendix: SurrealDB Primitives Used

| Primitive | Used By | Purpose |
|-----------|---------|---------|
| `DEFINE TABLE ... CHANGEFEED nNd` | `ChangeTracking()` | Enable `SHOW CHANGES` replay |
| `SHOW CHANGES FOR TABLE ... SINCE ...` | Change replay | Replay/re-sync projection workers |
| `DEFINE EVENT ON TABLE ... THEN (...)` | `ChangeTracking()` | Automatic audit trail on CRUD |
| `$event`, `$before`, `$after`, `$value` | `DEFINE EVENT` access | Capture before/after state in triggers |
| `BEGIN TRANSACTION / COMMIT` | `Patch<T>()` pipeline | Atomic event append + state mutation |
| `DEFINE INDEX ... UNIQUE` | `EventStream<T>()` | Optimistic concurrency on stream versions |
| `DEFINE FIELD ... TYPE` | Schema management | Typed columns for all tables |
| `SchemaDiffer` / `INFO FOR TABLE` | Schema migration | Detect schema drift and apply ALTER |

---

## Appendix B: Implementation Plan & Tracking

> **Last reviewed:** 2026-07-08  
> **Reviewers:** Oracle (architecture), Architect (implementation)  
> **Status:** Synthesis complete — Phases 0-2 approved, Phase 3 scoped down

### Key Review Decisions

| Decision | Value | Source |
|----------|-------|--------|
| `IConfigureAeroDB` | Keep — no rename | Oracle P2 |
| `.WithReason()` | Confirmed (not `.Because()`) | Design review |
| `ChangeTracking()` | Explicit opt-in per entity | User decision |
| `EventLog` rename | Rename to `Outbox` before Phase 1 | Oracle C1 |
| Source gen for projections | Pure `Apply(TEvent e)` convention on `partial class` — no attributes | User decision |
| `DEFINE EVENT` | Use `IF NOT EXISTS` not `OVERWRITE` | Architect Issue #2, Oracle Q6 |
| Phase 3 PatchPipeline | **Deferred** — ship `.WithReason()` only | Architect Issue #1, Oracle Q2 |

### Benefits Summary

| Capability | Before | After |
|------------|--------|-------|
| **Audit trail** | Per-doc metadata only (CreatedAt/LastModified) | Full before/after snapshots in `aero_audit_log` via native `DEFINE EVENT`, with `$auth.id` and timestamp |
| **Change replay** | None (no document-level time-travel) | `CHANGEFEED` + `SHOW CHANGES` enables per-document point-in-time queries |
| **Event stream config** | Manual `IEvents.Append(streamId, events)` with string-based types | Typed `.Events<AccountOpened, MoneyDeposited>()` — compile-time safety |
| **Command intent** | `Patch<T>().Increment(x.Balance, 50m)` — raw mutation, no business meaning | Same call + `.WithReason("CustomerDeposit")` → emits `MoneyDeposited` domain event |
| **Transactional safety** | State mutation and event append are separate calls | Single SurrealDB transaction: append event + apply patch + fire audit triggers |
| **Change detection** | Manual state diff in C# | `UPDATE ... SET field += value RETURN DIFF` gives JSON Patch `[{"op":"replace","path":"/balance","value":175}]` |
| **Projection dispatch** | Runtime reflection discovers `Apply(TEvent e)` methods | Source gen emits `switch` dispatcher at compile time — AOT-compatible |
| **Concurrency** | Manual version management | `UNIQUE(stream_id, version)` index enforced at DB level, optimistic locking |
| **Multi-tenancy** | `tenantId` column (risk of leak if filter is forgotten) | Structurally isolated via SurrealDB namespace + database scoping |

### Fluent Query Examples

```csharp
// ── Phase 1: ChangeTracking audit trail ──────────────────────────

// Minimal — sensible defaults
Schema.For<Account>()
    .ChangeTracking();

// Full configuration
Schema.For<Account>(doc =>
{
    doc.ChangeTracking(x => x
        .ChangeFeed(retention: "30d", includeOriginal: true)
        .AuditTrail()
        .Outbox()
        .TrackCreates()
        .TrackUpdates()
        .TrackDeletes()
        .Ignore(a => a.UpdatedAt));
});

// Query audit log
await session.Query<AuditLogRecord>()
    .Where(a => a.SourceTable == "account")
    .Where(a => a.SourceRecord == accountId)
    .Where(a => a.ChangedAt > threeDaysAgo)
    .OrderBy(a => a.ChangedAt)
    .ToListAsync();

// ── Phase 2: Typed event streams ──────────────────────────────────

Schema.EventStream<Account>()
    .UseTable<AccountEvent>()
    .Events<AccountOpened, MoneyDeposited, MoneyWithdrawn, BalanceCorrected>()
    .StreamId(x => x.StreamId)
    .Version(x => x.Version)
    .Serialization(EventSerializationMode.Json);

// Append with compile-time type safety
await session.Events
    .For<Account>(accountId)
    .AppendAsync(new MoneyDeposited(accountId, 50m), expectedVersion: 3);

// Streaming read with checkpoint
await foreach (var envelope in session.Events
    .ReadFromAsync<AccountEvent>(checkpoint, ct))
{
    Process(envelope);
}

// ── Phase 3: Intent-aware patch with WithReason() ──────────────

// Application code
await session.Patch<Account>(accountId)
    .Increment(x => x.Balance, 50m)
    .WithReason("CustomerDeposit")
    .SaveChangesAsync();

// Configuration mapping reason → domain event
Schema.For<Account>(doc =>
{
    doc.PatchEvents(events =>
    {
        events.OnIncrement(x => x.Balance)
            .WithReason("CustomerDeposit")
            .Emit((acct, amount, ctx) =>
                new MoneyDeposited(acct.Id, amount, ctx.UserId));

        events.OnDecrement(x => x.Balance)
            .WithReason("Withdrawal")
            .Emit((acct, amount, ctx) =>
                new MoneyWithdrawn(acct.Id, amount, ctx.UserId));

        events.OnReplace(x => x.Balance)
            .WithReason("Correction")
            .When((b, a, ctx) => ctx.Reason == "Correction")
            .Emit((before, after, ctx) =>
                new BalanceCorrected(
                    after.Id, before.Balance, after.Balance, ctx.UserId));
    });
});

// ── Combined: All layers together ─────────────────────────────

public sealed class BankingAeroConfig : IConfigureAeroDB
{
    public void Configure(IAeroSchemaBuilder schema)
    {
        // 1. Document with change tracking + patch events
        schema.For<Account>(doc =>
        {
            doc.ChangeTracking(x => x
                .AuditTrail()
                .ChangeFeed("30d")
                .Ignore(a => a.UpdatedAt));

            doc.PatchEvents(events =>
            {
                events.OnIncrement(x => x.Balance)
                    .WithReason("CustomerDeposit")
                    .Emit((acct, amt, ctx) =>
                        new MoneyDeposited(acct.Id, amt, ctx.UserId));

                events.OnDecrement(x => x.Balance)
                    .WithReason("Withdrawal")
                    .Emit((acct, amt, ctx) =>
                        new MoneyWithdrawn(acct.Id, amt, ctx.UserId));
            });
        });

        // 2. Event stream
        schema.EventStream<Account>()
            .UseTable<AccountEvent>()
            .Events<AccountOpened, MoneyDeposited, MoneyWithdrawn, BalanceCorrected>()
            .StreamId(x => x.StreamId)
            .Version(x => x.Version);

        // 3. Projection (source gen — pure convention)
        schema.Projection<AccountBalanceProjection>()
            .FromStream<AccountEvent>()
            .Handles<AccountOpened>()
            .Handles<MoneyDeposited>()
            .Handles<MoneyWithdrawn>()
            .Checkpointed()
            .Async();
    }
}
```

### Phase 0 — `IAeroSchemaBuilder` (foundation)

| File | Status | Notes |
|------|--------|-------|
| `src/AeroDB/Configuration/IAeroSchemaBuilder.cs` | 📋 Planned | Fluent facade: `For<T>()`, `EventStream<T>()`, `Projection<T>()` |
| `src/AeroDB/Configuration/AeroDBSchemaBuilder.cs` | 📋 Planned | Internal implementation wrapping `SchemaOptions` |
| `src/AeroDB/Configuration/IConfigureAeroDB.cs` | 📋 Planned | Add `Configure(IAeroSchemaBuilder)` default-implement overload |

### Phase 1 — `ChangeTracking()` (native audit + changefeed)

| File | Status | Notes |
|------|--------|-------|
| `src/AeroDB/ChangeTracking/ChangeTrackingOptions.cs` | 📋 Planned | Config POCO: `ChangeFeed`, `AuditTrail`, `TrackCreates/Updates/Deletes`, `Ignore`, `Outbox` |
| `src/AeroDB/ChangeTracking/ChangeTrackingGenerator.cs` | 📋 Planned | Returns `ChangeTrackingDefinition` POCO with table name, CHANGEFEED clause, trigger definitions |
| `src/AeroDB/ChangeTracking/ChangeTrackingSchemaManager.cs` | 📋 Planned | Creates shared `aero_audit_log` table, calls `EventTriggerManager.EnsureTriggerAsync` |
| `src/AeroDB/ChangeTracking/AuditLogRecord.cs` | 📋 Planned | POCO for `aero_audit_log` |
| `src/AeroDB/ChangeTracking/TechnicalEventTypes.cs` | 📋 Planned | `AccountCreated`/`AccountUpdated`/`AccountDeleted` sync event types |
| `src/AeroDB/Schema/DocumentMapping.cs` | 📋 Planned | Add `.ChangeTracking()` method |
| `src/AeroDB/Schema/SchemaManager.cs` | 📋 Planned | Add `AlterTableChangeFeedAsync()` for CHANGEFEED DDL |
| `src/AeroDB/Events/EventTriggerManager.cs` | 📋 Planned | Add `IF NOT EXISTS` support (default), optional `Overwrite` flag |

### Phase 2 — `EventStreamConfiguration` (typed event streams)

| File | Status | Notes |
|------|--------|-------|
| `src/AeroDB/EventStreams/EventStreamConfiguration.cs` | 📋 Planned | Fluent: `.UseTable<T>()`, `.Events<>()`, `.StreamId()`, `.Version()` |
| `src/AeroDB/EventStreams/EventStreamGenerator.cs` | 📋 Planned | DDL: `DEFINE TABLE ... SCHEMAFULL CHANGEFEED` + `DEFINE INDEX ... UNIQUE` |
| `src/AeroDB/EventStreams/EventStreamAppender.cs` | 📋 Planned | Typed append API wrapping existing `IEvents` |
| `src/AeroDB/EventStreams/IEventStreamAppender.cs` | 📋 Planned | Interface |
| `src/AeroDB/Events/IEvents.cs` | 📋 Planned | Add `Append<T>(string, T, ...)` typed overloads with default implementations |

### Phase 3-Lite — `WithReason()` + `PatchEvents()` config (scope-limited)

| File | Status | Notes |
|------|--------|-------|
| `src/AeroDB/Patching/PatchEventsConfiguration.cs` | 📋 Planned | `.OnIncrement()`, `.OnReplace()`, `.Emit()` fluent API |
| `src/AeroDB/Patching/PatchRuleResolver.cs` | 📋 Planned | Expression tree matching against `PatchEventRule` registry |
| `src/AeroDB/Patching/PatchContext.cs` | 📋 Planned | Context: user ID, reason, metadata, timestamp |
| `src/AeroDB/Patching/PatchEventRule.cs` | 📋 Planned | Rule model: op + field + reason + emit factory |
| `src/AeroDB/Patching/IPatchExpression.cs` | 📋 Planned | Add `.WithReason(string reason)` method |
| `src/AeroDB/Patching/PatchExpression.cs` | 📋 Planned | Implement `.WithReason()` — stores reason metadata, no pipeline execution |
| `src/AeroDB/Schema/DocumentMapping.cs` | 📋 Planned | Add `.PatchEvents()` method |
| `src/AeroDB/Schema/SchemaManager.cs` | 📋 Planned | Wire rule registration during `InitializeAsync` |

> **Note:** The full `PatchPipeline` (load doc → validate version → resolve rule → compute version → append event → apply patch in a single atomic transaction) is **deferred**. Current `SaveChangesAsync` pipeline architecture cannot interleave event appends with patch mutations atomically. Requires a SaveChangesAsync refactoring in a future milestone. `.WithReason()` ships as metadata-only — the reason string is stored on the patch context for listeners and audit, but no automatic event emission occurs.

### Phase 4 — Projection Source Gen (future, post-v1)

| File | Status | Notes |
|------|--------|-------|
| `src/AeroDB.SourceGenerators/ProjectionDispatchGenerator.cs` | 📋 Future | Convention: `Apply(TEvent e)` → `switch` dispatcher on `partial class` |
| `src/AeroDB.SourceGenerators/ProjectionRegistrationGenerator.cs` | 📋 Future | DI registration for discovered projections |
| `src/AeroDB.SourceGenerators/EventTypeMetadataGenerator.cs` | 📋 Future | Event type name constants for string-free dispatch |

### Marten Event Sourcing Parity Audit

Audit of **47 Marten event sourcing features** against AeroDB (source: marten submodule + marten-llms-full.txt):

#### ✅ Already Existing (30 of 47 — 64%)

| Feature | File |
|---------|------|
| `Append` / `StartStream` (all overloads) | `Events/EventStore.cs` |
| `AppendOptimistic` / `AppendExclusive` | `Events/IEvents.cs` |
| `FetchStream` with version/timestamp/filtering | `Events/EventStore.cs` |
| `AggregateStream<T>()` | `Events/LiveStreamAggregation.cs` |
| `FetchForWriting` + auto-flush in SaveChanges | `Events/FetchForWritingResult.cs`, `DocumentSession.cs` |
| `FetchLatest<T>()` | On `IQuerySession` |
| `OverwriteEvent` / `DeleteSingleEvent` | `Events/EventStore.cs` |
| `FetchStreamState` (Guid + string) | `Events/EventStore.cs` |
| `CompactStream` / `ArchiveStream` / `WriteTombstone` | `Events/EventStore.cs` |
| `FetchAllAfterSequence` | Async daemon polling |
| `SingleStreamProjection<T>` (1 type param) | `Projections/` |
| `MultiStreamProjection<T>` | `Projections/` |
| `EventProjection<T>` (generic) | `Projections/` |
| `SnapshotProjection<T>` | `Projections/` |
| `FlatTableProjection<TDoc, TId>` | `Projections/` |
| `CompositeProjection` | `Projections/` (Phase 18) |
| `LiveStreamAggregation<T>` | `Events/` |
| `IAggregateGrouper<TId>` / `CustomGrouping` | `Projections/` (Phase 18) |
| `IEventSlice<T>` enrichment | `Projections/IEventSlice.cs` (Phase 17) |
| `IChangeListener` (daemon pipeline) | `Diagnostics/IChangeListener.cs` (Phase 17) |
| `BulkInsertEventsAsync` (IEvents + store-level) | `Events/EventStore.cs` (Phase 13) |
| `SessionOptions` class | `SessionOptions.cs` (Phase 13) |
| `RequestCount` on sessions | `InternalSessionBase.cs` (Phase 13) |
| Per-session logger override | `IMartenSessionLogger` (Phase 13) |
| Per-session listeners | `SessionOptions.Listeners` (Phase 13) |
| Identity map (`Eject<T>` / `EjectAll`) | `InternalSessionBase.cs` |
| Event metadata masking (GDPR) | `StoreOptions.DataMaskingPredicate` (Phase 17) |
| Projection progress persistence | `AsyncDaemon` + `mt_projection_progress` (Phase 7b) |
| Sharded async daemon | `AsyncDaemon` (Phase 18) |
| Event type index for rebuilds | `StoreOptions.Events.EnableEventTypeIndex` |

#### ⚠️ Partial (5 of 47 — 11%)

| Feature | Gap |
|---------|-----|
| `DocumentTracking.DirtyTracking` | Auto-diff comparison logic in `SaveChangesAsync` is dead code — factory exists, identity map exists, but diff against original snapshot is not wired |
| `SingleStreamProjection<TDoc, TId>` (2 type params) | Only `SingleStreamProjection<T>` exists — missing typed stream identity |
| `EventProjection` (non-generic) | Only `EventProjection<T>` exists — missing non-generic base class |
| `ProjectionCollection` fluent API | `StoreOptions.Projections` is raw `List<IProjection>` — missing `Add<T>()`/`Snapshot<T>()`/`LiveStreamAggregation<T>()` |
| Blue-green projections (`ProjectionVersionAttribute`) | `SubscriptionVersion` exists on subscriptions only — no `ProjectionVersionAttribute` for projections |

#### ❌ Missing (10 of 47 — 21%)

| Feature | Notes |
|---------|-------|
| **Natural Keys** (`[NaturalKey]`, `FetchForWriting<T,TKey>`) | Marten attribute + typed overload by natural key instead of stream ID |
| **DCB / Dynamic Consistency Boundary** (`FetchForWritingByTags`, `IEventBoundary`, event tags) | Cross-stream consistency — `EventTagQuery` exists for reads only |
| **Poison event detection / dead-letter queue** | Async daemon has no skip-and-log or DLQ for bad events |
| **`IChangeSet.Changes()` before/after** | `IChangeSet` exists (Operations, AppendedEvents) but no `Changes()` returning `IChange<T>` with `.Before`/`.After` |
| **`FetchEventStoreStatistics()`** | Event count, stream count, etc. — missing from `IAeroDBAdvanced` |
| **`AllProjectionProgress()` / `ProjectionProgressFor(ShardName)`** | Per-shard progress diagnostics — missing from `IAeroDBAdvanced` |
| **Projection rebuild progress callback** | `RebuildAsync` has no progress reporting |
| **Tenant management (Add/Remove tenants via Advanced)** | Missing from `IAeroDBAdvanced` |
| **Database pool management** | Postgres-specific — marked N/A |
| **Strong typed stream IDs (Vogen/StronglyTypedId)** | `EventStreamIdentity` is internal, no public API for custom stream identity types |

### SurrealDB Primitives Verified

All primitives tested against live SurrealDB (memory engine, 2026-07-07):

| Primitive | Works | Notes |
|-----------|-------|-------|
| `UPDATE ... SET field += value RETURN DIFF` | ✅ | `[{"op":"replace","path":"/balance","value":175}]` — native arithmetic, no read-then-compute |
| `UPDATE ... PATCH [{op,path,value}]` | ✅ | RFC 6902 JSON Patch — `add`, `replace`, `remove`, `copy`, `move` |
| `UPDATE ... RETURN DIFF` | ✅ | Returns JSON Patch diff array |
| `UPDATE ... RETURN AFTER` | ✅ | Single keyword only |
| `DEFINE TABLE ... CHANGEFEED 30d INCLUDE ORIGINAL` | ✅ | `INFO FOR TABLE` shows changefeed config |
| `DEFINE EVENT ON TABLE WHEN $event THEN (...)` | ✅ | All three triggers (CREATE/UPDATE/DELETE) fire correctly |
| `$before`, `$after`, `$value`, `$event` in triggers | ✅ | Audit log entries created with operation, source, timestamps |
| `CREATE aero_audit_log SET ...` inside trigger | ✅ | Shared audit log table + population verified |

### SurrealDB Version Dependency

- **SurrealDB ≥ 3.1.0 recommended** — CVE fix for JSON Patch info-disclosure via `UPDATE … PATCH` `copy` op with empty `from` pointer
- **`DEFINE EVENT ... ASYNC`** requires SurrealDB 3.0+. For audit purposes, sync triggers are preferable anyway (in-transaction vs. out-of-transaction)
- **`CHANGEFEED` retention enforcement** varies by storage engine: RocksDB/TiKV/SurrealDB Cloud enforce; Memory/SurrealKV accept syntax but don't clean up

### Open Risks (from reviews)

| # | Risk | Severity | Status |
|---|------|----------|--------|
| R1 | `UNIQUE(stream_id, version)` + `BEGIN/COMMIT` atomicity must be validated against live SurrealDB | Critical | ⏳ Pre-Phase 1 gate |
| R2 | `.When()` predicate evaluates against snapshot isolation — stale read without optimistic concurrency | Medium | Documented: require `UseOptimisticConcurrency = true` for safety-critical predicates |
| R3 | `aero_audit_log` unbounded growth — no retention strategy | Medium | Deferred to post-v1 |
| R4 | `PatchContext` metadata (reason, userId) doesn't flow to audit log via `DEFINE EVENT` | Medium | Store in document field or `LET $patch_reason` session variable |
| R5 | Source gen misses inherited `Apply()` methods from base classes | Low | Documented: stub `Apply` methods in partial class that delegate to base |
| R6 | `CHANGEFEED` on event stream tables doubles storage | Low | Make opt-in for event stream tables |
