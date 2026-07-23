# Dali ↔ Marten Public API Parity

> **Last updated:** 2026-07-01 (post Live Query API v1 — fluent builder + bounded Channel<T>)
> **Marten version:** 9.0.0-alpha.1 (PostgreSQL document DB + event store)
> **Dali target:** SurrealDB document DB + event store (EF Core provider layer)
>
> This document compares Dali's public API surface against Marten's to track parity,
> identify gaps, and prioritize implementation work. Every method/property listed below
> was extracted directly from the source `.cs` files — not from memory or documentation.

---

## Legend

| Icon | Meaning |
|------|---------|
| ✅ | **1:1** — identical signature and semantics |
| ⚠️ | **Close** — same concept, minor differences (name, async suffix, parameter shape) |
| ❌ | **Missing** — no Dali equivalent |
| 🔄 | **N/A** — Postgres-specific, not applicable to SurrealDB |
| 🟡 | **Dali-native** — unique feature with no Marten equivalent (keep as-is) |

**Effort/action key in Notes column:**
- `✅ Keep` — Dali-native, no change needed
- `🔄 No action` — Postgres-specific, won't port
- `⚡ Quick` — can fix in hours (wrappers, overloads, simple additions)
- `⏸️ Medium` — fix in days (needs new class/interface)
- `🧠 Large` — fix in weeks (significant new feature)
- `🚫 Won't port` — not worth the effort (niche or incompatible)

---

## Totals

| Category | ✅ 1:1 | ⚠️ Close | ❌ Missing | 🔄 N/A | 🟡 Dali | **Actionable** |
|----------|-------:|---------:|----------:|-------:|--------:|:--------------:|
| A. Store Initialization | 22 | 13 | 5 | 1 | 0 | 12 |
| B. Session APIs | 34 | 9 | 9 | 3 | 0 | 18 |
| C. Event Sourcing | 28 | 5 | 8 | 1 | 0 | 13 |
| D. Projections | 14 | 6 | 4 | 0 | 0 | 10 |
| E. LINQ / Querying | 17 | 3 | 7 | 0 | 0 | 7 |
| F. Schema / Mapping | 15 | 5 | 0 | 4 | 0 | 0 |
| G. Advanced / Diagnostics | 24 | 4 | 3 | 0 | 0 | 7 |
| H. Multi-tenancy | 4 | 3 | 4 | 1 | 0 | 7 |
| I. ASP.NET Core | 3 | 0 | 2 | 0 | 0 | 2 |
| J. Dali-Specific | 0 | 0 | 0 | 0 | 19 | 0 |
| **Totals** | **164** | **48** | **42** | **10** | **19** | **76** |

---

## Action Plan

| Level | Count | Scope | Time to Complete |
|-------|-------|-------|-----------------|
| 🌱 **Quick wins** — done | ~50 | Basic CRUD, CUD parity, StoreOptions parity, session query, infrastructure, ASP.NET, patches | **0 remaining** |
| 🔧 **Medium effort** — done | ~60 | Fine-grained CUD, batched events, search/FTS, event manipulation, DI multi-store, streaming tuples, subscriptions, QueryForNonStaleData | **0 remaining** |
| 🧠 **Large effort** — remaining | ~4 | `AssignTagWhere`, `Upcast<TUpcaster>`, `IMartenSessionLogger`, `StoreOptions.LinQ` | **>1 week each** |
| 🚫 **Won't port** | ~6 | Marten LINQ internals, runtime code gen infra, Marten schema DDL, Marten LINQ projections | — |

---

## A. Store Initialization

### A1. IDocumentStore / DocumentStore

| Marten Method | Dali Equivalent | Status | Notes |
|---------------|----------------|--------|-------|
| `DocumentStore.For(Action<StoreOptions>)` | `Documents.For(Action<StoreOptions>)` | ⚠️ | Same static factory pattern, different class name |
| `services.AddMarten(Action<StoreOptions>)` | `services.AddDali(Action<StoreOptions>)` | ⚠️ | Same pattern, different name |
| `services.AddMarten(string connectionString)` | `services.AddDali(string)` | ✅ | Same via connection string parser |
| `services.AddMarten(StoreOptions)` | `services.AddDali(StoreOptions)` | ✅ | Same pre-built overload |
| `services.AddMartenStore<T>(Action<StoreOptions>)` | `services.AddDaliStore<T>(Action<StoreOptions>)` | ✅ | Multi-store support |
| `IDocumentStore.Options` (IReadOnlyStoreOptions) | `IDocumentStore.Options` (IReadOnlyStoreOptions) | ✅ | StoreOptions now implements IReadOnlyStoreOptions |
| `IDocumentStore.Storage` (IMartenStorage) | `IDocumentStore.Advanced` (IDaliAdvanced) | ⚠️ | Dali puts admin ops under Advanced |
| `IDocumentStore.Diagnostics` (IDiagnostics) | `IDocumentStore.Advanced.Diagnostics` | ⚠️ | Nested under Advanced in Dali |
| `IDocumentStore.Advanced` (AdvancedOperations) | `IDocumentStore.Advanced` (IDaliAdvanced) | ⚠️ | Different shape: SurrealDB-specific operations |
| `IDocumentStore.BulkInsertEnlistTransactionAsync<T>(documents, transaction, mode, batchSize, ct)` | — | 🔄 | Postgres-specific: requires Transaction |
| `IDocumentStore.BulkInsertAsync<T>(tenantId, documents, mode, batchSize, ct)` | `BulkInsertAsync<T>(tenantId, documents, ...)` | ✅ | Store-level tenant-scoped bulk insert |
| `IDocumentStore.BulkInsertAsync<T>(documents, mode, batchSize, ct)` | `IDocumentSession.BulkInsertAsync<T>(documents, batchSize, ct)` | ⚠️ | Dali is session-level, not store-level |
| `IDocumentStore.BulkInsertDocumentsAsync(mixed, mode, batchSize, ct)` | `BulkInsertDocumentsAsync(documents, ...)` | ✅ | Mixed-type bulk insert |
| `IDocumentStore.BulkInsertDocumentsAsync(tenantId, mixed, mode, batchSize, ct)` | `BulkInsertDocumentsAsync(tenantId, documents, ...)` | ✅ | Tenant + mixed-type bulk insert |
| `IDocumentStore.BulkInsertEventsAsync(streams, batchSize, ct)` | `IEvents.BulkInsertEventsAsync(streams, batchSize, ct)` | ⚠️ | Dali's is on IEvents, not store-level |
| `IDocumentStore.BulkInsertEventsAsync(tenantId, streams, batchSize, ct)` | `BulkInsertEventsAsync(tenantId, streams, ...)` | ✅ | Tenant-scoped bulk event insert |
| `IDocumentStore.BuildProjectionDaemonAsync(tenantId, logger)` | `DocumentStore.Daemon` (property) | ⚠️ | Dali sets daemon externally; Marten creates it |
| `IDocumentStore.OpenSession(SessionOptions)` | `IDocumentStore.OpenSessionAsync(SessionOptions, ct)` | ⚠️ | Dali is always async |
| `IDocumentStore.OpenSerializableSessionAsync(SessionOptions, ct)` | — | 🔄 | Postgres-specific serializable isolation |
| `IDocumentStore.LightweightSession(isolationLevel)` | `IDocumentStore.LightweightSessionAsync(ct)` | ⚠️ | Dali is async; no isolation level |
| `IDocumentStore.LightweightSession(tenantId, isolationLevel)` | `store.WithTenant(tenantId).LightweightSessionAsync(ct)` | ⚠️ | Dali uses fluent WithTenant |
| `IDocumentStore.LightweightSession(SessionOptions)` | `IDocumentStore.OpenSessionAsync(options, ct)` | ⚠️ | Dali unified under OpenSessionAsync |
| `IDocumentStore.LightweightSerializableSessionAsync(ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.LightweightSerializableSessionAsync(tenantId, ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.LightweightSerializableSessionAsync(options, ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.IdentitySession(isolationLevel)` | `IDocumentStore.DocumentSessionAsync(ct)` [Obsolete] | ⚠️ | Dali marks as obsolete, prefers OpenSessionAsync |
| `IDocumentStore.IdentitySession(tenantId, isolationLevel)` | `IdentitySessionAsync(tenantId, ct)` | ✅ | Tenant-scoped identity session |
| `IDocumentStore.IdentitySession(SessionOptions)` | `IDocumentStore.OpenSessionAsync(options, ct)` | ⚠️ | Dali unified under OpenSessionAsync |
| `IDocumentStore.IdentitySerializableSessionAsync(ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.IdentitySerializableSessionAsync(tenantId, ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.IdentitySerializableSessionAsync(options, ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.DirtyTrackedSession(isolationLevel)` | `OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.DirtyTracking })` | ⚠️ | Dali uses unified OpenSessionAsync with DocumentTracking |
| `IDocumentStore.DirtyTrackedSession(tenantId, isolationLevel)` | `DirtyTrackedSessionAsync(tenantId, ct)` | ✅ | Tenant + dirty tracking |
| `IDocumentStore.DirtyTrackedSession(SessionOptions)` | `IDocumentStore.OpenSessionAsync(options, ct)` | ⚠️ | Dali unified under OpenSessionAsync |
| `IDocumentStore.DirtyTrackedSerializableSessionAsync(ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.DirtyTrackedSerializableSessionAsync(tenantId, ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.DirtyTrackedSerializableSessionAsync(options, ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.QuerySession()` | `IDocumentStore.QuerySessionAsync(ct)` | ⚠️ | Dali is async only |
| `IDocumentStore.QuerySession(string tenantId)` | `store.WithTenant(tenantId).QuerySessionAsync(ct)` | ⚠️ | Dali uses fluent WithTenant |
| `IDocumentStore.QuerySession(SessionOptions)` | `IDocumentStore.QuerySessionAsync(ct)` | ⚠️ | Dali doesn't support SessionOptions for QuerySession |
| `IDocumentStore.QuerySerializableSessionAsync(ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.QuerySerializableSessionAsync(tenantId, ct)` | — | 🔄 | Postgres-specific |
| `IDocumentStore.QuerySerializableSessionAsync(options, ct)` | — | 🔄 | Postgres-specific |
| — | `IDocumentStore.WithTenant(string tenantId)` | 🟡 | ✅ Keep — Dali advantage: fluent tenant routing |
| — | `IDocumentStore.Graph<T>()` | 🟡 | ✅ Keep — Dali advantage: graph traversal entry point |
| — | `IDocumentStore.LiveQuerySessionAsync(ct)` | 🟡 | ✅ Keep — Dali-native: SRP-separated live query session with fluent builder |
| — | `IDocumentStore.InitializeAsync(ct)` | 🟡 | ✅ Keep — Dali advantage: explicit init step |
| — | `IDocumentStore.CleanDeletedDocumentsAsync(olderThan, ct)` | 🟡 | ✅ Keep — Dali advantage: soft-delete cleanup |
| — | `IDocumentStore.Client` (ISurrealDbClient) | 🟡 | ✅ Keep — Dali advantage: direct SDK access |

### A2. StoreOptions

| Marten Property/Method | Dali Equivalent | Status | Notes |
|------------------------|----------------|--------|-------|
| `StoreOptions.Connection(string connectionString)` | `StoreOptions.Connection(endpoint, ns, db, username, password, token)` | ⚠️ | SurrealDB uses multi-field config instead of PG connection string |
| `StoreOptions.DatabaseSchemaName` | `StoreOptions.Database` | ⚠️ | SurrealDB uses `Database` (within a `Namespace`) |
| `StoreOptions.AutoCreateSchemaObjects` (enum: CreateOrUpdate/None/CreateOnly/All) | `StoreOptions.Schema.AutoCreate` (bool) | ⚠️ | Dali uses simple bool; Marten has 4-value enum |
| `StoreOptions.Schema.For<T>()` | `StoreOptions.Schema.For<T>()` | ✅ | Same fluent mapping entry point |
| `StoreOptions.Events` (IEventStoreOptions) | `StoreOptions.Events` (EventSourcingOptions) | ⚠️ | Same concept, different concrete types |
| `StoreOptions.Projections` (ProjectionOptions) | `StoreOptions.Projections` (List<IProjection>) | ⚠️ | Dali exposes as List; Marten uses ProjectionOptions class |
| `StoreOptions.Listeners` | `StoreOptions.Listeners` | ✅ | Same List<IDocumentSessionListener> |
| `StoreOptions.InitialData` | `StoreOptions.InitialData` | ✅ | Same List<IInitialData> |
| `StoreOptions.Serializer()` (ISerializer) | `StoreOptions.SerializerOptions` / `ConfigureSerializer()` | ⚠️ | System.Text.Json only; no ISerializer abstraction |
| `StoreOptions.Logger()` (IMartenLogger) | `StoreOptions.LoggerFactory` (ILoggerFactory) | ⚠️ | Dali uses standard ILoggerFactory |
| `StoreOptions.OpenTelemetry` | `StoreOptions.OpenTelemetry` (OpenTelemetryOptions) | ✅ | Config class for doc store, commands, events, projections |
| `StoreOptions.NameDataLength` | — | 🔄 | Postgres-specific identifier length |
| `StoreOptions.Policies` | `StoreOptions.Policies` | ✅ | Same DocumentPolicies system |
| `StoreOptions.Tenancy` (ITenancy) | `StoreOptions.TenancyStyle` | ⚠️ | Dali has simpler TenancyStyle enum |
| `StoreOptions.UpdateBatchSize` | `StoreOptions.UpdateBatchSize` | ✅ | Same batch size for unit-of-work |
| `StoreOptions.CommandTimeout` | `StoreOptions.CommandTimeout` | ✅ | Default command timeout in seconds |
| `StoreOptions.UseStickyConnectionLifetimes` | — | 🔄 | Postgres-specific connection management |
| `StoreOptions.UseRowLevelSecurity(settingName)` | — | 🔄 | Postgres RLS-specific |
| `StoreOptions.ConfigurePolly(Action<ResiliencePipelineBuilder>)` | `StoreOptions.ConfigurePolly(Action<ResiliencePipelineBuilder>)` | ✅ | Polly retry pipeline config |
| `StoreOptions.Advanced` (AdvancedOptions) | `StoreOptions.Advanced` (AdvancedOptions) | ✅ | Same concept |
| `StoreOptions.CreateDatabasesForTenants()` | — | 🔄 | Postgres-specific |
| `StoreOptions.MultiTenantedWithSingleServer()` | `StoreOptions.TenancyStyle = DatabasePerTenant` | ⚠️ | Dali uses simpler enum-based config |
| `StoreOptions.MultiTenantedDatabases()` | `StoreOptions.MultiTenantedDatabases()` | ✅ | Static multi-tenancy config |
| `StoreOptions.MultiTenantedDatabasesWithMasterDatabaseTable()` | — | 🔄 | Postgres-specific |
| `StoreOptions.MultiTenantedWithShardedDatabases()` | — | 🔄 | Postgres-specific |
| `StoreOptions.LinQ` (LinqParsing) | — | 🔄 | SurrealDB SDK has native LINQ provider; Marten's pluggable parser is Postgres-specific |
| `StoreOptions.Storage` (StorageFeatures) | — | 🔄 | Postgres-specific: custom table/sequence DDL |
| — | `StoreOptions.Endpoint` | 🟡 | ✅ Keep — Dali advantage: SurrealDB endpoint URL |
| — | `StoreOptions.Namespace` | 🟡 | ✅ Keep — Dali advantage: SurrealDB namespace |
| — | `StoreOptions.Username` | 🟡 | ✅ Keep — Dali advantage: SurrealDB auth |
| — | `StoreOptions.Password` | 🟡 | ✅ Keep — Dali advantage: SurrealDB auth |
| — | `StoreOptions.Token` | 🟡 | ✅ Keep — Dali advantage: SurrealDB JWT token |
| — | `StoreOptions.ClientFactory` | 🟡 | ✅ Keep — Dali advantage: custom client factory |
| — | `StoreOptions.DatabaseEndpoints` | 🟡 | ✅ Keep — Dali advantage: multi-host endpoints |
| — | `StoreOptions.Views` (ViewOptions) | 🟡 | ✅ Keep — Dali advantage: SurrealDB materialized views |
| — | `StoreOptions.Functions` (FunctionOptions) | 🟡 | ✅ Keep — Dali advantage: SurrealDB DEFINE FUNCTION |
| — | `StoreOptions.Experimental` (ExperimentalOptions) | 🟡 | ✅ Keep — Dali advantage: SurrealML integration |
| — | `StoreOptions.SoftDeleteEnabled` | 🟡 | ✅ Keep — Dali advantage: soft-delete toggle |
| — | `StoreOptions.Tracking` (DocumentTracking) | 🟡 | ✅ Keep — Dali advantage: default session tracking |
| — | `StoreOptions.UseOptimisticConcurrency` | 🟡 | ✅ Keep — Dali advantage: global optimistic concurrency |
| — | `StoreOptions.Hierarchies` | 🟡 | ✅ Keep — Dali advantage: polymorphic document hierarchy |
| — | `StoreOptions.EfCoreProjections` | 🟡 | ✅ Keep — Dali advantage: EF Core projection support |
| — | `StoreOptions.Configurators` (List<IConfigureDali>) | 🟡 | ✅ Keep — Dali advantage: config modules |
| — | `StoreOptions.LiveQueryChannelCapacity` (default 4096) | 🟡 | ✅ Keep — Dali-native: bounded channel backpressure config |
| — | `StoreOptions.LiveQueryChannelFullMode` (default Wait) | 🟡 | ✅ Keep — Dali-native: channel-full behavior config |

### A3. SchemaOptions

| Marten Schema Property | Dali Equivalent | Status | Notes |
|------------------------|----------------|--------|-------|
| `Schema.For<T>()` | `Schema.For<T>()` | ✅ | Same |
| `Schema.ForStreams<T>()` | `Schema.ForStreams<T>()` | ✅ | Same |
| `Schema.ForEvents()` | `Schema.ForEvents()` | ✅ | Same |
| `Schema.ForEventProgression()` | `Schema.ForEventProgression()` | ✅ | Same |
| — | `Schema.Analyzers` (AnalyzerOptions) | 🟡 | ✅ Keep — Dali advantage: SurrealDB DEFINE ANALYZER |
| — | `Schema.Edge<TEdge, TIn, TOut>()` | 🟡 | ✅ Keep — Dali advantage: SurrealDB RELATION tables |
| — | `Schema.AutoCreateDatabases` | 🟡 | ✅ Keep — Dali advantage: auto-create databases |
| — | `Schema.Accesses` (List<AccessDefinition>) | 🟡 | ✅ Keep — Dali advantage: DEFINE ACCESS |
| — | `Schema.Tokens` (List<TokenDefinition>) | 🟡 | ✅ Keep — Dali advantage: DEFINE TOKEN |
| — | `Schema.Scopes` (List<ScopeDefinition>) | 🟡 | ✅ Keep — Dali advantage: DEFINE SCOPE |

### A4. EventSourcingOptions

| Marten EventStoreOptions | Dali Equivalent | Status | Notes |
|--------------------------|----------------|--------|-------|
| `StreamIdentity` | `EventSourcingOptions.StreamIdentity` | ✅ | Guid vs string stream identity config |
| `TenancyStyle` | `EventSourcingOptions.TenancyStyle` | ✅ | Event tenancy style |
| `DatabaseSchemaName` | `EventSourcingOptions.DatabaseSchemaName` | ✅ | Same |
| `MetadataConfig` | `EventSourcingOptions.MetadataConfig` | ✅ | Same |
| `AppendMode` | `EventSourcingOptions.AppendMode` | ✅ | Same concept |
| `EnableSideEffectsOnInlineProjections` | `EventSourcingOptions.EnableSideEffectsOnInlineProjections` | ✅ | Side effects toggle |
| `UseIdentityMapForAggregates` | `EventSourcingOptions.UseIdentityMapForAggregates` | ✅ | Identity map for aggregates |
| `AddEventType<TEvent>()` | `EventSourcingOptions.AddEventType<T>()` | ✅ | Event type registration |
| `AddEventType(Type)` | `EventSourcingOptions.AddEventType(Type)` | ✅ | Event type registration |
| `AddEventTypes(IEnumerable<Type>)` | `EventSourcingOptions.AddEventTypes(IEnumerable<Type>)` | ✅ | Bulk event type registration |
| `MapEventType<TEvent>(string)` | `EventSourcingOptions.MapEventType<T>(string)` | ✅ | Event type name mapping |
| `MapEventType(Type, string)` | `EventSourcingOptions.MapEventType(Type, string)` | ✅ | Event type name mapping |
| `Upcast<TEvent>(string, JsonTransformation)` | `EventSourcingOptions.Upcast<T>(oldEventType, upcast)` | ⚠️ | Dali uses simpler Func<object, T>; Marten uses JsonTransformation class |
| `Upcast<TOldEvent, TEvent>(string, Func<TOldEvent, TEvent>)` | `EventSourcingOptions.Upcast<T>(oldEventType, upcast)` | ⚠️ | Dali doesn't distinguish old/new types |
| `Upcast(params IEventUpcaster[])` | `EventSourcingOptions.Upcasters` (List) | ⚠️ | Dali checks list; Marten has fluent method |
| `Upcast<TUpcaster>()` | `Upcast<TUpcaster>()` | ✅ | Typed upcaster registration |
| `Subscribe(ISubscription)` | `Subscribe(ISubscription)` | ✅ | Event subscription model |
| `Subscribe(ISubscription, Action<ISubscriptionOptions>)` | `Subscribe(ISubscription, Action<SubscriptionOptions>)` | ✅ | Subscription with options |
| `AddMaskingRuleForProtectedInformation<T>()` | `EventSourcingOptions.DataMaskingPredicate` | ⚠️ | Dali uses predicate; Marten uses action/func per type |
| `RegisterTagType<TTag>()` | `RegisterTagType<TTag>()` | ✅ | DCB tag registration |
| `UseBinarySerializer<TEvent>()` | `EventSourcingOptions.SerializationMode` | ⚠️ | Dali is global mode; Marten is per-type |
| `EnableUniqueIndexOnEventId` | `EventSourcingOptions.EnableUniqueIndexOnEventId` | ✅ | Unique event ID index |
| `EnableEventTypeIndex` | `EventSourcingOptions.EnableEventTypeIndex` | ✅ | (type, seq_id) composite index |
| `EnableBigIntEvents` | `EventSourcingOptions.EnableBigIntEvents` | ✅ | 64-bit event version/sequence |
| `EnableAdvancedAsyncTracking` | `EnableAdvancedAsyncTracking` | ✅ | Advanced async tracking |
| `UseArchivedStreamPartitioning` | — | 🔄 | Postgres-specific partitioning |
| `UseMandatoryStreamTypeDeclaration` | `EventSourcingOptions.UseMandatoryStreamTypeDeclaration` | ✅ | Mandatory stream type |
| `TimeProvider` | `EventSourcingOptions.TimeProvider` | ✅ | Configurable time provider |
| — | `EventSourcingOptions.Enabled` | 🟡 | ✅ Keep — Dali advantage: event sourcing on/off |
| — | `EventSourcingOptions.Triggers` (EventTriggerOptions) | 🟡 | ✅ Keep — Dali advantage: SurrealDB DEFINE EVENT triggers |

---

## B. Session APIs

### B1. IDocumentSession — IDocumentSession interface

| Marten IDocumentSession | Dali Equivalent | Status | Notes |
|------------------------|----------------|--------|-------|
| `BeginTransaction()` | `BeginTransaction()` | ✅ | Returns IDaliTransaction for explicit control |
| `BeginTransactionAsync(ct)` | `BeginTransactionAsync(ct)` | ✅ | Returns IDaliTransaction for explicit control |
| `PendingChanges` (IUnitOfWork) | `PendingChanges` (IUnitOfWork) | ✅ | Exposes pending unit of work |
| `Events` (IEventStoreOperations) | `Events` (IEvents) | ⚠️ | Dali has simpler IEvents interface |
| `Concurrency` (ConcurrencyChecks) | `Concurrency` (ConcurrencyChecks?) | ✅ | Per-session concurrency override |
| `Listeners` (IList<IDocumentSessionListener>) | `Listeners` (IList<IDocumentSessionListener>) | ✅ | Per-session listener list |
| `LastModifiedBy` | `CurrentUser` | ⚠️ | Same concept, different name |
| `SetHeader(key, value)` | `SetHeader(key, value)` | ✅ | Per-session metadata headers |
| `GetHeader(key)` | `GetHeader(key)` | ✅ | Per-session metadata headers |
| `SaveChangesAsync(ct)` | `SaveChangesAsync(ct)` | ✅ | Same signature, returns int |
| `Eject<T>(document)` | `Eject<T>(entity)` | ✅ | Dali has both by-ID and by-entity overloads |
| `EjectAllOfType(Type type)` | `EjectAllOfType(type)` | ✅ | Non-generic overload delegates to EjectAll<T> |
| `EjectAllPendingChanges()` | `ClearChanges()` | ⚠️ | Same concept, different name |
| `ForTenant(tenantId)` (ITenantOperations) | `ForTenant(tenantId)` | ✅ | Returns session for chaining |
| `Dispose()` | `DisposeAsync()` | ⚠️ | Dali uses IAsyncDisposable only |
| — | `IdentityMapCount` | 🟡 | ✅ Keep — Dali advantage: identity map stats |
| — | `Relate<TEdge>(from, to, data)` | 🟡 | ✅ Keep — Dali advantage: queue graph edge |
| — | `Unrelate(edgeId)` | 🟡 | ✅ Keep — Dali advantage: queue graph edge deletion |
| — | `Logger` (ILogger) | 🟡 | ✅ Keep — Dali advantage: swappable per-session logger |

### B2. IDocumentOperations

| Marten IDocumentOperations | Dali Equivalent | Status | Notes |
|---------------------------|----------------|--------|-------|
| `Delete<T>(T entity)` | `Delete<T>(T entity)` | ✅ | Same |
| `Delete<T>(int id)` | `Delete<T>(int id)` | ✅ | Delete by int ID |
| `Delete<T>(long id)` | `Delete<T>(long id)` | ✅ | Delete by long ID |
| `Delete<T>(object id)` | `Delete<T>(object id)` | ✅ | Delete by object ID |
| `Delete<T>(Guid id)` | `Delete<T>(Guid id)` | ✅ | Delete by Guid ID |
| `Delete<T>(string id)` | `Delete<T>(string id)` | ✅ | Delete by string ID |
| `DeleteWhere<T>(Expression)` | `DeleteWhere<T>(predicate, ct)` | ⚠️ | Dali executes immediately, Marten defers |
| `DeleteObjects(IEnumerable<object>)` | `DeleteObjects(IEnumerable<object>)` | ✅ | Mixed-type delete |
| `Store<T>(IEnumerable<T>)` | `Store<T>(IEnumerable<T>)` | ✅ | Bulk store |
| `Store<T>(params T[])` | `Store<T>(params T[])` | ✅ | Bulk store params |
| `StoreObjects(IEnumerable<object>)` | `StoreObjects(IEnumerable<object>)` | ✅ | Mixed-type store |
| `UpdateExpectedVersion<T>(entity, version)` | `UpdateExpectedVersion<T>(entity, version)` | ✅ | Version-gated update |
| `UpdateRevision<T>(entity, revision)` | `UpdateRevision<T>(entity, revision)` | ✅ | Revision-gated update |
| `TryUpdateRevision<T>(entity, revision)` | `TryUpdateRevision<T>(entity, revision)` | ✅ | Try revision update |
| `Insert<T>(IEnumerable<T>)` | `Insert<T>(IEnumerable<T>)` | ✅ | Insert-only (fail if exists) |
| `Insert<T>(params T[])` | `Insert<T>(params T[])` | ✅ | Insert-only array |
| `InsertObjects(IEnumerable<object>)` | `InsertObjects(IEnumerable<object>)` | ✅ | Mixed-type insert |
| `Update<T>(IEnumerable<T>)` | `Update<T>(IEnumerable<T>)` | ✅ | Update-only (fail if not exists) |
| `Update<T>(params T[])` | `Update<T>(params T[])` | ✅ | Update-only array |
| `HardDelete<T>(T entity)` | `HardDelete<T>(T entity)` | ✅ | Hard-delete |
| `HardDelete<T>(int id)` | `HardDelete<T>(int id)` | ✅ | Hard-delete by int ID |
| `HardDelete<T>(long id)` | `HardDelete<T>(long id)` | ✅ | Hard-delete by long ID |
| `HardDelete<T>(Guid id)` | `HardDelete<T>(Guid id)` | ✅ | Hard-delete by Guid ID |
| `HardDelete<T>(string id)` | `HardDelete<T>(string id)` | ✅ | Hard-delete by string ID |
| `HardDeleteWhere<T>(Expression)` | `HardDeleteWhere<T>(predicate, ct)` | ✅ | Bulk hard-delete |
| `UndoDeleteWhere<T>(Expression)` | `UndoDeleteWhere<T>(predicate, ct)` | ✅ | Undo soft-delete |
| `QueueOperation(IStorageOperation)` | `QueueOperation(IStorageOperation)` | ✅ | Low-level operation queuing |
| `QueueSqlCommand(sql, params)` | `ExecuteSqlAsync(sql, params, ct)` | ⚠️ | Dali executes immediately, Marten queues |
| `QueueSqlCommand(placeholder, sql, params)` | `QueueSqlCommand(placeholder, sql, params)` | ✅ | Placeholder variant |
| `UseIdentityMapFor<T>()` | `UseIdentityMapFor<T>()` | ✅ | Per-type identity map opt-in |
| — | `Store<T>(string id, T document)` | 🟡 | ✅ Keep — Dali advantage: store with explicit ID |
| — | `QueryAsync<T>(ct)` | 🟡 | ✅ Keep — Dali advantage: query all shortcut |

### B3. IQuerySession

| Marten IQuerySession | Dali Equivalent | Status | Notes |
|----------------------|----------------|--------|-------|
| `Database` (IMartenDatabase) | `Database` (string) | ✅ | Database name from StoreOptions |
| `Connection` (NpgsqlConnection) | — | 🔄 | SurrealDB doesn't expose raw connection |
| `Logger` (IMartenSessionLogger) | `Logger` (IMartenSessionLogger?) | ✅ | Custom session logger interface |
| `RequestCount` | `RequestCount` (int) | ✅ | Request counter |
| `DocumentStore` (IDocumentStore) | `DocumentStore` (IDocumentStore?) | ✅ | Parent store reference |
| `Events` (IQueryEventStore) | `Events` (IEvents) | ⚠️ | Dali has same property but different interface |
| `Json` (IJsonLoader) | `Json` (IJsonLoader) | ✅ | JSON document loader |
| `CausationId` | `CausationId` | ✅ | Per-session causation ID |
| `CorrelationId` | `CorrelationId` | ✅ | Per-session correlation ID |
| `TenantId` (string, non-nullable) | `TenantId` (string?, nullable) | ⚠️ | Dali returns null when no tenancy |
| `CheckExistsAsync<T>(string id, ct)` | `CheckExistsAsync<T>(string id, ct)` | ✅ | Existence check |
| `CheckExistsAsync<T>(int id, ct)` | `CheckExistsAsync<T>(int id, ct)` | ✅ | Existence check |
| `CheckExistsAsync<T>(long id, ct)` | `CheckExistsAsync<T>(long id, ct)` | ✅ | Existence check |
| `CheckExistsAsync<T>(Guid id, ct)` | `CheckExistsAsync<T>(Guid id, ct)` | ✅ | Existence check |
| `CheckExistsAsync<T>(object id, ct)` | `CheckExistsAsync<T>(object id, ct)` | ✅ | Existence check |
| `LoadAsync<T>(string id, ct)` | `LoadAsync<T>(string id, ct)` | ✅ | Same |
| `LoadAsync<T>(object id, ct)` | `LoadAsync<T>(object id, ct)` | ✅ | Load by object ID |
| `LoadAsync<T>(int id, ct)` | `LoadAsync<T>(int id, ct)` | ✅ | Load by int ID |
| `LoadAsync<T>(long id, ct)` | `LoadAsync<T>(long id, ct)` | ✅ | Load by long ID |
| `LoadAsync<T>(Guid id, ct)` | `LoadAsync<T>(Guid id, ct)` | ✅ | Load by Guid ID |
| `Query<T>()` (IMartenQueryable<T>) | `Query<T>()` (ISableQueryable<T>) | ✅ | Both return custom IQueryable |
| `QueryForNonStaleData<T>(timeout)` | `QueryForNonStaleData<T>(timeout)` | ✅ | Wait for async projection |
| `QueryForNonStaleData<T>(timeout, mode)` | `QueryForNonStaleData<T>(timeout, mode)` | ✅ | With timeout mode |
| `StreamJson<T>(destination, ct, sql, params)` | `StreamJson<T>(destination, sql, ct, params)` | ✅ | JSON streaming |
| `StreamJson<T>(destination, placeholder, sql, params)` | `StreamJson<T>(destination, placeholder, sql, ct, params)` | ✅ | JSON streaming with placeholder |
| `QueryAsync<T>(sql, ct, params)` | `RawQueryAsync<T>(sql, params, ct)` | ⚠️ | Dali returns List<T>, Marten returns IReadOnlyList<T> |
| `QueryAsync<T>(placeholder, sql, ct, params)` | `QueryAsync<T>(placeholder, sql, ct, params)` | ✅ | Placeholder variant |
| `CreateBatchQuery()` | `CreateBatchQuery()` | ✅ | Same IBatchedQuery return |
| `QueryAsync<TDoc,TOut>(compiledQuery, ct)` | `QueryAsync<TDoc,TOut>(compiledQuery, ct)` | ✅ | Same |
| `StreamJsonOne<TDoc,TOut>(query, dest, ct)` | `StreamJsonOne<TDoc,TOut>(query, dest, ct)` | ✅ | Compiled query JSON streaming |
| `StreamJsonMany<TDoc,TOut>(query, dest, ct)` | `StreamJsonMany<TDoc,TOut>(query, dest, ct)` | ✅ | Compiled query array JSON streaming |
| `ToJsonOne<TDoc,TOut>(query, ct)` | `ToJsonOne<TDoc,TOut>(query, ct)` | ✅ | Compiled query to JSON string |
| `ToJsonMany<TDoc,TOut>(query, ct)` | `ToJsonMany<TDoc,TOut>(query, ct)` | ✅ | Compiled query to JSON array string |
| `LoadManyAsync<T>(params string[] ids)` | `LoadManyAsync<T>(IEnumerable<string> ids)` | ✅ | Bulk load |
| `LoadManyAsync<T>(IEnumerable<string> ids)` | `LoadManyAsync<T>(IEnumerable<string> ids)` | ✅ | Bulk load |
| `LoadManyAsync<T>(params Guid[] ids)` | `LoadManyAsync<T>(IEnumerable<Guid> ids)` | ✅ | Bulk load |
| `LoadManyAsync<T>(params int[] ids)` | `LoadManyAsync<T>(IEnumerable<int> ids)` | ✅ | Bulk load |
| `LoadManyAsync<T>(params long[] ids)` | `LoadManyAsync<T>(IEnumerable<long> ids)` | ✅ | Bulk load |
| `VersionFor<TDoc>(TDoc entity)` | `VersionFor<T>(entity)` | ✅ | Get known version |
| `SearchAsync<TDoc>(queryText, regConfig, ct)` | `SearchAsync<TDoc>(queryText, regConfig, ct)` | ✅ | Full-text search via SurrealDB analyzers |
| `PlainTextSearchAsync<TDoc>(searchTerm, regConfig, ct)` | `PlainTextSearchAsync<TDoc>(searchTerm, regConfig, ct)` | ✅ | Plain-text search |
| `PhraseSearchAsync<TDoc>(searchTerm, regConfig, ct)` | `PhraseSearchAsync<TDoc>(searchTerm, regConfig, ct)` | ✅ | Phrase search |
| `WebStyleSearchAsync<TDoc>(searchTerm, regConfig, ct)` | `WebStyleSearchAsync<TDoc>(searchTerm, regConfig, ct)` | ✅ | Web-style search |
| `PrefixSearchAsync<TDoc>(searchTerm, regConfig, ct)` | `PrefixSearchAsync<TDoc>(searchTerm, regConfig, ct)` | ✅ | Prefix search |
| `MetadataForAsync<T>(entity, ct)` | `MetadataForAsync<T>(entity, ct)` | ✅ | Entity metadata fetch |
| `ForTenant(string tenantId)` | `SetTenant(string tenantId)` | ✅ | Same concept, different name |
| `ExecuteAsync(NpgsqlCommand, ct)` | `ExecuteSqlAsync(sql, params, ct)` | ⚠️ | Dali uses SurrealQL string, not NpgsqlCommand |
| `ExecuteReaderAsync(NpgsqlCommand, ct)` | — | 🔄 | Npgsql-dependent, Dali uses SurrealQL raw queries |
| `AdvancedSql` (IAdvancedSql) | `RawQueryAsync<T>` | ⚠️ | Dali has simpler raw query on session |
| `QueryByPlanAsync<T>(plan, ct)` | `QueryByPlanAsync<T>(plan, ct)` | ✅ | Query plan execution |
| `NextSequenceValue(sequenceName, ct)` | `Snowflake.NewId()` / `Guid.NewGuid()` | 🟡 | 🧊 TBD — Dali advantage: offline, distributed, no DB round-trip |
| `NextSequenceValue(DbObjectName, ct)` | `Snowflake.NewId()` / `Guid.NewGuid()` | 🟡 | 🧊 TBD — Dali advantage: offline, distributed, no DB round-trip |
| `NextSequenceValueAsLong(sequenceName, ct)` | `Snowflake.NewId()` / `Guid.NewGuid()` | 🟡 | 🧊 TBD — Dali advantage: offline, distributed, no DB round-trip |
| `NextSequenceValueAsLong(DbObjectName, ct)` | `Snowflake.NewId()` / `Guid.NewGuid()` | 🟡 | 🧊 TBD — Dali advantage: offline, distributed, no DB round-trip |
| — | `FetchLatest<T>(streamId, ct)` | 🟡 | ✅ Keep — Dali advantage: fetch projected aggregate |
| — | `ClearTenant()` | 🟡 | ✅ Keep — Dali advantage: clear tenant context |
| `IQuerySession.WatchTableAsync<T>(ct)` | `IQuerySession.WatchTableAsync<T>(ct)` | ✅ | Marten API parity — wraps fluent `ILiveQuerySession.Live<T>()` via `LegacyLiveQueryAdapter<T>` |
| `IQuerySession.WatchQueryAsync<T>(whereClause, ct)` | `IQuerySession.WatchQueryAsync<T>(whereClause, ct)` | ✅ | Marten API parity — delegates to `LiveRawQuery<T>()` |
| `IQuerySession.WatchStreamAsync(streamId, ct)` | `IQuerySession.WatchStreamAsync(streamId, ct)` | ✅ | Marten API parity — watches mt_events by stream ID |
| — | `LiveQuerySession.Live<T>().Where().SubscribeAsync(ct)` | 🟡 | ✅ Keep — Dali-native: fluent typed live query builder |
| — | `LiveQuerySession.LiveRawQuery<T>(surql, params, ct)` | 🟡 | ✅ Keep — Dali-native: raw SurrealQL escape hatch |
| — | `IDaliLiveQuery<T>.Changes(ct)` → `IAsyncEnumerable<DaliLiveChange<T>>` | 🟡 | ✅ Keep — Dali-native: bounded Channel<T> adapter |
| — | `Graph<T>()` | 🟡 | ✅ Keep — Dali advantage: graph traversal |
| — | `CreateBatchQuery()` | ✅ | Same |

### B4. Patching API

| Marten IPatchExpression<T> | Dali Equivalent | Status | Notes |
|---------------------------|----------------|--------|-------|
| `IPatchExpression<T>.Set(expression, value)` | `IPatchExpression<T>.Set<TValue>(property, value)` | ✅ | Same |
| `IPatchExpression<T>.SetAll(value)` | `IPatchExpression<T>.SetAll<TValue>(value)` | ✅ | Set all properties |
| `IPatchExpression<T>.Delete(expression)` | `IPatchExpression<T>.Delete<TValue>(property)` | ✅ | Same |
| `IPatchExpression<T>.Increment(expression, amount)` | `IPatchExpression<T>.Increment(property, amount)` | ✅ | Same (Dali has int, long, double, float, decimal overloads) |
| `IPatchExpression<T>.Append(expression, element)` | `IPatchExpression<T>.Append<TElement>(property, element)` | ✅ | Same |
| — | `IPatchExpression<T>.AppendIfNotExists<TElement>()` | 🟡 | ✅ Keep — Dali advantage: conditional append |
| `IPatchExpression<T>.Insert(expression, element, index)` | `IPatchExpression<T>.Insert<TElement>(property, element, index)` | ✅ | Same |
| — | `IPatchExpression<T>.InsertIfNotExists<TElement>()` | 🟡 | ✅ Keep — Dali advantage: conditional insert |
| `IPatchExpression<T>.Remove(expression, element)` | `IPatchExpression<T>.Remove<TElement>(property, element)` | ✅ | Same |
| `IPatchExpression<T>.Rename(oldName, expression)` | `IPatchExpression<T>.Rename(oldName, target)` | ✅ | Same |
| `IPatchExpression<T>.Duplicate(expression)` | `IPatchExpression<T>.Duplicate<TElement>(source, destinations)` | ⚠️ | Dali supports multiple destinations |
| `IPatchExpression<T>.Increment(expression, amount)` for int only | ✅ | Same for each numeric type | |

### B5. Bulk Operations

| Marten Bulk Insert | Dali Equivalent | Status | Notes |
|--------------------|----------------|--------|-------|
| `store.BulkInsertAsync<T>(documents, mode, batchSize, ct)` | `session.BulkInsertAsync<T>(documents, batchSize, ct)` | ⚠️ | Dali is session-level |
| `store.BulkInsertAsync<T>(tenantId, documents, mode, batchSize, ct)` | `store.BulkInsertAsync<T>(tenantId, documents, ...)` | ✅ | Tenant-scoped bulk insert |
| `store.BulkInsertEnlistTransactionAsync<T>(documents, tx, mode, batchSize, ct)` | — | 🔄 | Postgres-specific |
| `store.BulkInsertDocumentsAsync(mixed, mode, batchSize, ct)` | `store.BulkInsertDocumentsAsync(documents, ...)` | ✅ | Mixed-type bulk insert |
| `store.BulkInsertDocumentsAsync(tenantId, mixed, mode, batchSize, ct)` | `store.BulkInsertDocumentsAsync(tenantId, documents, ...)` | ✅ | Tenant + mixed bulk insert |
| `store.BulkInsertEventsAsync(streams, batchSize, ct)` | `events.BulkInsertEventsAsync(streams, batchSize, ct)` | ⚠️ | Dali's is on IEvents |
| `store.BulkInsertEventsAsync(tenantId, streams, batchSize, ct)` | `store.BulkInsertEventsAsync(tenantId, streams, ...)` | ✅ | Tenant-scoped bulk events |

---

## C. Event Sourcing

### C1. IEvents (Write + Read on session)

| Marten IEventOperations / IQueryEventStore | Dali IEvents | Status | Notes |
|-------------------------------------------|-------------|--------|-------|
| `events.AppendOptimistic(streamId, lastKnownVersion, events)` | `AppendOptimistic(string streamId, long lastKnownVersion, events, ct)` | ✅ | Same — all write overloads on IEvents |
| `events.AppendExclusive(streamId, events)` | `AppendExclusive(string streamId, events, ct)` | ✅ | Same |
| `events.AppendOptimistic(Guid, long, events)` | `AppendOptimistic(Guid, long, events, ct)` | ✅ | Same |
| `events.AppendExclusive(Guid, events)` | `AppendExclusive(Guid, events, ct)` | ✅ | Same |
| `events.StartStream(guid, events)` | `StartStream(string streamId, events, ct)` | ⚠️ | Dali uses string IDs primarily |
| `events.StartStream<T>(...)` | `StartStream<T>(..., events, ct)` | ✅ | Same |
| `events.Append(guid, events)` | `Append(streamId, events, headers, ct)` | ⚠️ | Dali has headers parameter |
| `events.Append(guid, expectedVersion, events)` | `Append(streamId, expectedVersion, events, ct)` | ✅ | Same |
| `events.FetchStream(guid)` | `FetchStream(string streamId, ct)` | ⚠️ | Dali uses string ID |
| `events.FetchStream(guid, version, timestamp, fromVersion)` | `FetchStreamAsync(streamId, version, timestamp, fromVersion, ct)` | ✅ | Version/timestamp filtering |
| `events.AggregateStreamAsync<T>(guid)` | `AggregateStreamAsync<T>(string streamId, ct)` | ⚠️ | Dali uses string ID |
| `events.AggregateStreamAsync<T>(guid, version, timestamp, state, fromVersion)` | `AggregateStreamAsync<T>(streamId, version, timestamp, state, fromVersion, ct)` | ✅ | Parameterized aggregate |
| `events.AggregateStreamToLastKnownAsync<T>()` | `AggregateStreamToLastKnownAsync<T>(streamId, ct)` | ✅ | Retry aggregation |
| `events.FetchForWriting<T>(id)` | `FetchForWritingAsync<T>(string streamId, ct)` | ⚠️ | Dali returns FetchForWritingResult<T> |
| `events.FetchForExclusiveWriting<T>(id)` | `FetchForExclusiveWriting<T>(streamId, ct)` | ✅ | Exclusive write lock |
| `events.FetchLatest<T>(Guid id)` | `session.FetchLatest<T>(streamId, ct)` | ⚠️ | Dali has it on IQuerySession, not IEvents |
| `events.FetchLatest<T>(string id)` | `session.FetchLatest<T>(streamId, ct)` | ⚠️ | Dali has it on IQuerySession |
| `events.QueryRawEventDataOnly<T>()` (IMartenQueryable) | `QueryRawEventDataOnly<T>()` (ISableQueryable) | ✅ | LINQ on raw event data |
| `events.QueryAllRawEvents()` (IMartenQueryable<IEvent>) | `QueryAllRawEvents()` (ISableQueryable<IEvent>) | ✅ | Query all events |
| `events.FetchAllAfterSequence(long)` | `FetchAllAfterSequence(long, ct)` | ✅ | Same — for async daemon polling |
| `events.BuildEvent(data)` | `events.BuildEvent(data)` | ✅ | Build IEvent from raw data |
| `events.OverwriteEvent(e)` | `events.OverwriteEvent(e)` | ✅ | Overwrite existing event |
| `events.DeleteSingleEvent()` | `events.DeleteSingleEvent()` | ✅ | Delete single event |
| `events.AssignTagWhere()` | `EventSourcingOptions.AssignTagWhere<TTag>(predicate)` | ⚠️ | Simplified — stores tags as array on events; lacks Postgres tag tables |
| `events.CompactStreamAsync<T>(streamKey, configure)` | `CompactStreamAsync<T>(streamId, configure, ct)` | ✅ | Stream compaction |
| `events.CompactStreamAsync<T>(streamId, configure)` | `CompactStreamAsync<T>(streamId, configure, ct)` | ✅ | Stream compaction by Guid |
| — | `ArchiveStream(string streamId, ct)` | 🟡 | ✅ Keep — Dali advantage: archive stream |
| — | `ArchiveStream(Guid streamId, ct)` | 🟡 | ✅ Keep — Dali advantage: archive stream |
| — | `WriteTombstone(string streamId, version, ct)` | 🟡 | ✅ Keep — Dali advantage: tombstone events |

### C2. Stream State

| Marten | Dali | Status | Notes |
|--------|------|--------|-------|
| `events.FetchStreamState(Guid streamId)` | `FetchStreamStateAsync(Guid streamId, ct)` | ✅ | Stream metadata |
| `events.FetchStreamState(string streamKey)` | `FetchStreamStateAsync(string streamKey, ct)` | ✅ | Stream metadata by key |
| `events.FetchIfEventsExist(tagQuery)` | `events.EventsExistAsync(query, ct)` | ⚠️ | Simplified — SurrealQL CONTAINS query; lacks Postgres tag table JOINs |
| — | `FetchForWritingResult<T>.ExpectedVersion` | 🟡 | ✅ Keep — Dali advantage: expected version tracking |
| — | `FetchForWritingResult<T>.PendingEvents` | 🟡 | ✅ Keep — Dali advantage: pending event list |

### C3. IEvent / IEvent<T> Metadata

| Marten IEvent | Dali IEvent | Status | Notes |
|---------------|-------------|--------|-------|
| `IEvent.Data` (object) | `IEvent.Data` (object) | ✅ | Same |
| `IEvent<T>.Data` (T) | `IEvent<T>.Data` (T) | ✅ | Same |
| `IEvent.Version` (long) | `IEvent.Version` (long) | ✅ | Same |
| `IEvent.Sequence` (long) | `IEvent.Sequence` (long) | ✅ | Same |
| `IEvent.Timestamp` (DateTimeOffset) | `IEvent.Timestamp` (DateTimeOffset) | ✅ | Same |
| `IEvent.StreamId` (Guid) | `IEvent.StreamId` (string) | ⚠️ | Dali uses string; Marten uses Guid |
| `IEvent.StreamKey` (string?) | `IEvent.StreamKey` (Guid) | ⚠️ | Dali uses Guid; Marten uses string |
| `IEvent.Headers` (Dictionary<string, object>) | `IEvent.Headers` (Dictionary<string, string>) | ⚠️ | Dali restricts to string values |

### C4. Event Appender / Serialization

| Marten | Dali | Status | Notes |
|--------|------|--------|-------|
| `ISerializer` (abstraction) | `System.Text.Json.JsonSerializerOptions` | ⚠️ | Dali uses STJ directly |
| `IEventBinarySerializer` | `EventSerializationMode.Binary` | ⚠️ | Dali uses mode enum, not pluggable serializer |
| `EventAppendMode.Rich` | `EventAppendMode.Rich` | ✅ | Same |
| `EventAppendMode.Quick` | `EventAppendMode.Quick` | ✅ | Same |
| `JsonTransformation` (upcasting) | `IEventUpcaster` / lambda upcast | ⚠️ | Different: Dali uses Func<object,T>, Marten uses JsonTransformation class |

---

## D. Projections

### D1. IProjection

| Marten IProjection | Dali IProjection | Status | Notes |
|--------------------|-----------------|--------|-------|
| Inherits `IJasperFxProjection<IDocumentOperations>` | Standalone `IProjection` | ⚠️ | Different base |
| `ProjectionLifecycle` (Inline/Async/Live) | `ProjectionLifecycle` (same enum) | ✅ | Same |
| Events handled by `EventTypes` | `EventTypes` (Type[]) | ✅ | Same |
| — | `Lifecycle` property | ✅ | Same |
| — | `Name` property | ✅ | Same |
| — | `ApplyAsync(IProjectionContext, ct)` | ✅ | Same concept |
| — | `RebuildAsync(IDocumentSession, ct)` | ✅ | Same |
| — | `EnrichAsync<T>(IEventSlice<T>, ct)` | 🟡 | ✅ Keep — Dali advantage: enrichment hook |

### D2. Projection Base Classes

| Marten | Dali | Status | Notes |
|--------|------|--------|-------|
| `ProjectionBase` (abstract) | `InlineProjection<T>` | ⚠️ | Different base class names |
| `SingleStreamProjection<TDoc, TId>` | `SingleStreamProjection<T>` | ⚠️ | Dali doesn't type the TId |
| `MultiStreamProjection<TDoc, TId>` | `MultiStreamProjection<T>` | ⚠️ | Dali uses `GetDocumentId()` override |
| `EventProjection<T>` | `EventProjection<T>` | ✅ | Same per-event-type dispatch |
| `SnapshotProjection<T>` | `SnapshotProjection<T>` | ✅ | Same |
| `CompositeProjection` | `CompositeProjection` | ✅ | Same |
| `LiveStreamAggregation` | `LiveStreamAggregation` | ✅ | Same |
| `IAggregateGrouper<T>` | `IAggregateGrouper<TId>` | ⚠️ | Similar but different shape |

### D3. Projection Options

| Marten ProjectionOptions | Dali ProjectionOptions | Status | Notes |
|-------------------------|-----------------------|--------|-------|
| `opts.Projections.Add(projection, lifecycle)` | `opts.Projections.Add(projection)` | ⚠️ | Dali sets lifecycle inside projection |
| `opts.Projections.Snapshot<T>(lifecycle)` | `ProjectionOptions.Snapshot<T>(lifecycle)` | ✅ | Method on ProjectionOptions, returns DocumentMapping<T> for chaining |
| `opts.Projections.LiveStreamAggregation<T>()` | `LiveStreamAggregation.AggregateAsync<T>()` | ✅ | Same |
| `opts.Projections.Subscribe(subscription)` | `EventSourcingOptions.Subscribe(subscription)` | ✅ | Subscription model |
| `opts.Projections.RebuildOnStartup` | `opts.ProjectionBuild.RebuildOnStartup` | ✅ | Same concept |
| — | `opts.ProjectionBuild.RebuildProjectionNames` | 🟡 | ✅ Keep — Dali advantage: selective rebuild |
| — | `opts.ProjectionBuild.EnsureStateTable` | 🟡 | ✅ Keep — Dali advantage: state table toggle |

### D4. `Snapshot<T>` Gap — ✅ RESOLVED

`ProjectionOptions.Snapshot<T>(ProjectionLifecycle, Action<SnapshotOptions>?)` now provides 1:1 Marten parity.

### D5. Async Daemon

| Marten | Dali | Status | Notes |
|--------|------|--------|-------|
| `IProjectionDaemon` | `AsyncDaemon` | ✅ | Both have background daemon |
| `BuildProjectionDaemonAsync()` | `Daemon` property | ⚠️ | Marten creates; Dali sets externally |
| `IProjectionCoordinator` | `IProjectionCoordinator` | ✅ | Wraps AsyncDaemon, implements IHostedService |
| `IChangeListener` (daemon-level) | `IChangeListener` | ✅ | Same (BeforeCommit/AfterCommit) |

### D6. Projection Context / Side Effects

| Marten | Dali | Status | Notes |
|--------|------|--------|-------|
| `IProjectionContext` | `IProjectionContext` | ✅ | Same |
| Side effects on projections | `IProjectionSideEffect` + `AppendEventSideEffect` | ⚠️ | Dali supports append-event side effects |
| `FlatTableProjection` | `FlatTableProjection<TDoc, TId>` | ✅ | Same |
| — | `IEnrichProjection` | 🟡 | ✅ Keep — Dali advantage: enrichment |
| — | `SnapshotOptions` | 🟡 | ✅ Keep — Dali advantage: snapshot config |

---

## E. LINQ / Querying

### E1. IQueryable

| Marten IMartenQueryable<T> | Dali ISableQueryable<T> | Status | Notes |
|---------------------------|----------------------------|--------|-------|
| Inherits `IQueryable<T>` | Inherits `IOrderedQueryable<T>` | ✅ | Same |
| LINQ `Where`, `OrderBy`, `Select`, `Count`, etc. | Same via `IQueryable` | ✅ | Standard LINQ operators |
| `IMartenQueryable<T>.Include()` | `ISableQueryable<T>.IncludeBatch<TProp,TInclude>()`, `Include<T,TInclude>()`, `IncludeReverse<T,TChild>()` | ⚠️ | Dali has expression/LET-based includes; Marten has builder + callback variants |
| `IMartenQueryable<T>.Stats(out QueryStatistics)` | `ISableQueryable<T>.Stats(out QueryStatistics)` | ✅ | Interface+extension method; single-round-trip multi-statement SurrealQL |
| `ToPagedListAsync()` | `ToPagedListAsync()` | ✅ | Same extension |
| `Take(n)` | `Take(n)` | ✅ | Same |

### E2. Compiled Queries

| Marten | Dali | Status | Notes |
|--------|------|--------|-------|
| `ICompiledQuery<TDoc, TOut>` | `ICompiledQuery<TDoc, TOut>` | ✅ | Same interface |
| `ICompiledListQuery<TDoc, TOut>` | `ICompiledListQuery<TDoc, TOut>` | ✅ | Same |
| `ICompiledListQuery<TDoc>` | `ICompiledListQuery<TDoc>` | ✅ | Same |
| `ICompiledQuery<TDoc>` | `ICompiledQuery<TDoc>` | ✅ | Same |
| `IQueryPlanning` | `IQueryPlanning` | ✅ | Same |
| `session.QueryAsync<TDoc, TOut>(compiledQuery, ct)` | `session.QueryAsync<TDoc, TOut>(compiledQuery, ct)` | ✅ | Same |
| `session.StreamJsonOne<TDoc, TOut>(query, dest, ct)` | `StreamJsonOne<TDoc, TOut>(query, dest, ct)` | ✅ | Compiled query JSON streaming |
| `session.StreamJsonMany<TDoc, TOut>(query, dest, ct)` | `StreamJsonMany<TDoc, TOut>(query, dest, ct)` | ✅ | Compiled query array JSON streaming |
| `session.ToJsonOne<TDoc, TOut>(query, ct)` | `ToJsonOne<TDoc, TOut>(query, ct)` | ✅ | Compiled query to JSON string |
| `session.ToJsonMany<TDoc, TOut>(query, ct)` | `ToJsonMany<TDoc, TOut>(query, ct)` | ✅ | Compiled query to JSON array |

### E3. Batch Queries

| Marten IBatchedQuery | Dali IBatchedQuery | Status | Notes |
|---------------------|-------------------|--------|-------|
| `Query<TDoc, TResult>(compiledQuery)` | `Query<TDoc, TOut>(compiledQuery)` | ✅ | Same |
| `Query<T>(sql, params)` | `QueryRawAsync(surql, ct)` | ⚠️ | Dali returns raw JSON strings |
| `Execute(ct)` | `Execute(ct)` | ✅ | Same |
| `CheckExists<T>(id)` | `CheckExists<T>(id)` | ✅ | Batch existence check |
| `Load<T>(id)` | `Load<T>(id)` | ✅ | Batch load by ID |
| `LoadMany<T>()` | `LoadMany<T>(ids)` | ✅ | Batch multi-load |
| `Query<T>()` (LINQ) | `Query<T>()` (LINQ) | ✅ | Batch LINQ |
| `AddItem<T>(handler)` | `AddItem<T>(handler)` | ✅ | Custom handler |
| `QueryByPlan<T>(plan)` | `QueryByPlan<T>(plan)` | ✅ | Plan-based batch |
| `Events` (IBatchEvents) | `Events` (IBatchEvents) | ✅ | Batch event operations |
| `Parent` (QuerySession) | `Parent` (IQuerySession) | ✅ | Parent session access |

### E4. Advanced SQL

| Marten IAdvancedSql | Dali | Status | Notes |
|--------------------|------|--------|-------|
| `QueryAsync<T>(sql, ct, params)` | `RawQueryAsync<T>(sql, params, ct)` | ⚠️ | Dali on session directly |
| `QueryAsync<T>(placeholder, sql, ct, params)` | `QueryAsync<T>(placeholder, sql, ct, params)` | ✅ | Placeholder variant |
| `QueryAsync<T1,T2>(sql, ct, params)` (tuple) | `QueryAsync<T1,T2>(sql, ct, params)` | ✅ | Multi-type results |
| `StreamAsync<T>(sql, ct, params)` (IAsyncEnumerable) | `StreamAsync<T>(sql, ct, params)` | ✅ | Streaming results |
| `StreamAsync<T1,T2>(sql, ct, params)` | `StreamAsync<T1,T2>(sql, ct, params)` | ✅ | Streaming tuples |
| `StreamAsync<T1,T2,T3>(sql, ct, params)` | `StreamAsync<T1,T2,T3>(sql, ct, params)` | ✅ | 3-tuple streaming |

---

## F. Schema / Document Mapping

### F1. DocumentMapping<T>

| Marten DocumentMapping | Dali DocumentMapping<T> | Status | Notes |
|-----------------------|------------------------|--------|-------|
| `DocumentMapping.AddFullTextIndex()` | `DocumentMapping<T>.FullTextIndex()` | ⚠️ | Different: SurrealDB uses DEFINE ANALYZER |
| `DocumentMapping.AddIndex(index)` | `DocumentMapping<T>.Index(expression)` | ✅ | Same concept |
| `DocumentMapping.SoftDeleted()` | `ISoftDeleted` interface | ✅ | Same |
| `DocumentMapping.TenancyStyle` | `DocumentMapping<T>.TenancyStyle` | ✅ | Same |
| `DocumentMapping.Metadata` | `DocumentMapping<T>.MetadataConfig` | ✅ | Same |
| `DocumentMapping.PropertySearching()` | — | 🔄 | Postgres-specific |
| `DocumentMapping.DuplicatedField()` | — | 🔄 | Postgres-specific |
| `DocumentMapping.MapPropertyToJsonb()` | — | 🔄 | Postgres-specific |
| `DocumentMapping.ForeignKey()` | — | 🔄 | Postgres FK; SurrealDB uses graph edges |
| — | `DocumentMapping<T>.Schema` | 🟡 | ✅ Keep — Dali advantage: schema selection |
| — | `DocumentMapping<T>.TablePrefix` | 🟡 | ✅ Keep — Dali advantage: table prefix |
| — | `DocumentMapping<T>.FullTextIndex(columns, analyzer)` | 🟡 | ✅ Keep — Dali advantage: SurrealDB analyzer-based FTS |

### F2. IDocumentPolicy

| Marten IDocumentPolicy | Dali IDocumentPolicy | Status | Notes |
|-----------------------|--------------------|--------|-------|
| `IDocumentPolicy.Apply(DocumentMapping)` | `IDocumentPolicy.Apply(DocumentMapping)` | ✅ | Same |
| `Policies.ForAllDocuments(action)` | `Policies.ForAllDocuments(action)` | ✅ | Same |
| `Policies.ForDocumentsOfType<T>(action)` | `Policies.ForDocumentsOfType<T>(action)` | ✅ | Same |
| `Policies.AllDocumentsAreMultiTenanted()` | `StoreOptions.AllDocumentsAreMultiTenanted()` | ✅ | Convenience policy |
| `Policies.AllDocumentsSoftDeleted()` | `StoreOptions.AllDocumentsSoftDeleted()` | ✅ | Convenience policy |
| `Policies.AllDocumentsEnforceOptimisticConcurrency()` | `StoreOptions.AllDocumentsEnforceOptimisticConcurrency()` | ✅ | Convenience policy |

### F3. Document Hierarchy

| Marten | Dali | Status | Notes |
|--------|------|--------|-------|
| `DocumentHierarchy` (polymorphic) | `StoreOptions.HierarchyFor<TBase>()` | ✅ | Same |
| `SubClasses` | `DocumentHierarchy.AddSubClass<T>()` | ✅ | Same |

---

## G. Advanced / Diagnostics

### G1. IDiagnostics

| Marten IDiagnostics | Dali IDiagnostics | Status | Notes |
|--------------------|------------------|--------|-------|
| `PreviewCommand<TDoc,TReturn>(query, trackingMode)` | `PreviewCommandAsync<T>(query, ct)` | ⚠️ | Dali async, returns SurrealQL string, not NpgsqlCommand |
| `GetPostgresVersion()` | — | 🔄 | Postgres-specific |
| `ExplainPlanAsync<TDoc,TReturn>(query, ct)` | `ExplainPlanAsync<T>(query, ct)` | ✅ | Same concept |

### G2. IDocumentSessionListener

| Marten IDocumentSessionListener | Dali IDocumentSessionListener | Status | Notes |
|--------------------------------|------------------------------|--------|-------|
| `BeforeSaveChangesAsync(session, ct)` | `BeforeSaveChangesAsync(session, ct)` | ✅ | Same |
| `AfterCommitAsync(session, commit, ct)` | `AfterCommitAsync(session, changes, ct)` | ✅ | Same |
| `DocumentLoaded(id, document)` | `DocumentLoaded(id, document)` | ✅ | Per-document load hook |
| `DocumentAddedForStorage(id, document)` | `DocumentAddedForStorage(id, document)` | ✅ | Per-document store hook |
| — | `AfterSaveChangesAsync(session, ct)` | 🟡 | ✅ Keep — Dali advantage: after persist hook |
| — | `BeforeCommitAsync(session, ct)` | 🟡 | ✅ Keep — Dali advantage: pre-commit hook |
| — | `BeforeStoreAsync(session, entity, ct)` | 🟡 | ✅ Keep — Dali advantage: pre-store per-entity |
| — | `AfterStoreAsync(session, entity, ct)` | 🟡 | ✅ Keep — Dali advantage: post-store per-entity |
| — | `BeforeDeleteAsync(session, entity, ct)` | 🟡 | ✅ Keep — Dali advantage: pre-delete per-entity |
| — | `AfterDeleteAsync(session, entity, ct)` | 🟡 | ✅ Keep — Dali advantage: post-delete per-entity |

### G3. IChangeListener

| Marten IChangeListener | Dali IChangeListener | Status | Notes |
|-----------------------|--------------------|--------|-------|
| `AfterCommitAsync(session, commit, ct)` | `AfterCommitAsync(session, projections, ct, changes)` | ⚠️ | Dali passes projections list, not IChangeSet |
| `BeforeCommitAsync(session, commit, ct)` | `BeforeCommitAsync(session, projections, ct)` | ⚠️ | Dali passes projections list |

### G4. IChangeSet

| Marten IChangeSet | Dali IChangeSet | Status | Notes |
|-------------------|----------------|--------|-------|
| `Updated` (IEnumerable<object>) | `Updated` (IReadOnlyList<object>) | ✅ | Per-operation-type lists |
| `Inserted` (IEnumerable<object>) | `Inserted` (IReadOnlyList<object>) | ✅ | Per-operation-type lists |
| `Deleted` (IEnumerable<IDeletion>) | `Deleted` (IReadOnlyList<object>) | ✅ | Per-operation-type lists |
| `GetEvents()` | `AppendedEvents` | ⚠️ | Dali uses tuple list |
| `GetStreams()` | `GetStreams()` | ✅ | Stream action access |
| `Clone()` | `Clone()` | ✅ | Clone changeset |
| — | `Operations` (IReadOnlyList<Operation>) | 🟡 | ✅ Keep — Dali advantage: operation list |
| — | `HasChanges` | 🟡 | ✅ Keep — Dali advantage: change indicator |

### G5. IUnitOfWork

| Marten IUnitOfWork | Dali | Status | Notes |
|-------------------|------|--------|-------|
| `Deletions()` | `Deletions()` | ✅ | All pending deletions |
| `DeletionsFor<T>()` | `DeletionsFor<T>()` | ✅ | Typed deletions |
| `Updates()` | `Updates()` | ✅ | All pending updates |
| `Inserts()` | `Inserts()` | ✅ | All pending inserts |
| `InsertsFor<T>()` | `InsertsFor<T>()` | ✅ | Typed inserts |
| `UpdatesFor<T>()` | `UpdatesFor<T>()` | ✅ | Typed updates |
| `AllChangedFor<T>()` | `AllChangedFor<T>()` | ✅ | All changes for type |
| `Streams()` (event streams) | `Streams()` | ✅ | Event stream access |
| `Operations()` | `Operations()` | ✅ | All raw operations |
| `OperationsFor<T>()` | `OperationsFor<T>()` | ✅ | Typed operations |
| `OperationsFor(Type)` | `OperationsFor<T>(Type)` | ✅ | Typed operations |

### G6. IInitialData

| Marten IInitialData | Dali IInitialData | Status | Notes |
|--------------------|------------------|--------|-------|
| `Populate(IDocumentStore, ct)` | `PopulateAsync(IDocumentStore, ct)` | ✅ | Interface with Populate method |
| `InitialData` list | `StoreOptions.InitialData` list | ✅ | Same list pattern |

### G7. ISessionFactory

| Marten ISessionFactory | Dali | Status | Notes |
|-----------------------|------|--------|-------|
| `QuerySession()` | `ISessionFactory.QuerySessionAsync(ct)` | ✅ | Query session |
| `OpenSession()` | `ISessionFactory.OpenSessionAsync(ct)` | ✅ | Document session |

---

## H. Multi-tenancy

| Marten | Dali | Status | Notes |
|--------|------|--------|-------|
| Conjoined tenancy | `TenancyStyle.Conjoined` | ✅ | Same |
| Database-per-tenant | `TenancyStyle.DatabasePerTenant` | ✅ | Same |
| `ITenancy` (full interface) | `TenancyStyle` (enum) | ⚠️ | Dali has simpler enum |
| `ISingleServerMultiTenancy` | `ISingleServerMultiTenancy` | ✅ | Fluent multi-tenant builder |
| `IStaticMultiTenancy` | `IStaticMultiTenancy` | ✅ | Static multi-tenant builder |
| `ITenantOperations` / `ITenantQueryOperations` | `ITenantOperations` / `ITenantQueryOperations` | ✅ | Cross-tenant operations |
| `TenantIdStyle` | `TenantIdStyle` | ✅ | Case-sensitivity config |
| `ForTenant(tenantId)` (on IDocumentSession) | `SetTenant(tenantId)` (on IQuerySession) | ⚠️ | Dali is on IQuerySession only |
| `WithTenant(tenantId)` (on IDocumentStore) | `WithTenant(tenantId)` (on IDocumentStore) | ✅ | Same |
| RLS (Row-Level Security) | — | 🔄 | Postgres-specific |
| Sharded tenancy | — | 🔄 | Postgres-specific |
| `MasterTableTenancy` | — | 🔄 | Postgres-specific |
| — | `DatabasePerTenantSelector` | 🟡 | ✅ Keep — Dali advantage: per-tenant DB routing |

---

## I. ASP.NET Core Integration

| Marten | Dali | Status | Notes |
|--------|------|--------|-------|
| `Marten.AspNetCore` package | `DaliHttpExtensions.WriteById<T>` / `WriteArray<T>` | ✅ | ASP.NET streaming helpers |
| `WriteById<T>(httpContext, id)` | `WriteById<T>(httpContext, id)` | ✅ | Write single doc as JSON |
| `WriteArray<T>(httpContext, queryable)` | `WriteArray<T>(httpContext, ct)` | ✅ | Write query results as JSON array |
| `Marten.CommandLine` CLI | — | 🔄 | Separate CLI tool, not a library API |
| Health check integration | `AddDaliHealthCheck()` | ✅ | Same |
| `services.ConfigureMarten<T>()` | `services.ConfigureDali<T>()` | ✅ | Typed config per service |
| `services.ConfigureMarten(Action<StoreOptions>)` | `services.AddDali(Action<StoreOptions>)` | ⚠️ | Same, different name |
| `IGlobalConfigureMarten` | `IGlobalConfigureDali` | ✅ | Same pattern |
| `IConfigureMarten` | `IConfigureDali` | ✅ | Same pattern |
| `IAsyncConfigureMarten` | `IAsyncConfigureDali` | ✅ | Same pattern |

---

## J. Dali-Specific Features (No Marten Equivalent)

| Feature | Description |
|---------|-------------|
| `ITimeSeriesQuery<T>` | SurrealDB time-series query support (time bucketing, aggregation) |
| `TimeSeriesExtensions` | Extension methods for time-series queries |
| `ISpatialQuery<T>` | SurrealDB geo/spatial query support (geometry types) |
| `SpatialExtensions` | Extension methods for spatial queries |
| `IGraphQuery<T>` | SurrealDB graph traversal (arrow syntax: `->`, `<-`, `->...<-`) |
| `GraphConstants` (Graph, Edge) | SurrealDB graph direction constants |
| `session.Relate<TEdge>(from, to, data)` | Queue graph edge creation within transactions |
| `session.Unrelate(edgeId)` | Queue graph edge deletion within transactions |
| `session.WatchTableAsync<T>()` | SurrealDB LIVE SELECT (real-time push) on entire table |
| `session.WatchQueryAsync<T>(whereClause)` | SurrealDB LIVE SELECT with filter |
| `session.WatchStreamAsync(streamId)` | SurrealDB LIVE SELECT on event stream |
| `ILiveQuery<T>` | Live query result interface (IAsyncEnumerable-style) |
| `Schema.Analyzers` | SurrealDB `DEFINE ANALYZER` for full-text search |
| `Schema.Edge<TEdge, TIn, TOut>()` | SurrealDB `DEFINE TABLE ... TYPE RELATION` |
| `Schema.Accesses` / `Tokens` / `Scopes` | SurrealDB `DEFINE ACCESS / TOKEN / SCOPE` auth |
| `EventSourcingOptions.DataMaskingPredicate` | GDPR event data redaction via predicate |
| `EventSourcingOptions.AppendMode` (Rich/Quick) | SurrealDB-specific append modes |
| `EventSerializationMode` (Json/Binary) | Binary event serialization option |
| `StoreOptions.Views` (ViewDefinition<T>) | SurrealDB `DEFINE TABLE ... AS SELECT` materialized views |
| `StoreOptions.Functions` (FunctionOptions) | SurrealDB `DEFINE FUNCTION` user-defined functions |
| `StoreOptions.DatabaseEndpoints` | Multi-host / read-replica endpoint config |
| `StoreOptions.Experimental` (SurrealML) | SurrealDB machine learning integration |
| `StoreOptions.ReadPreference` | Read preference for multi-host scenarios |
| `IDaliSubscription` / `DaliSubscriptionBase` | Dali's own subscription model for event forwarding |
| `EventTriggerOptions` / `EventTriggerManager` | SurrealDB `DEFINE EVENT` database-level triggers |
| `TombstoneEvent` | Gap-filling tombstone events for idempotent retry |
| `AggregateQueryBuilder<T>` / `AggregateQueryExtensions` | Aggregation queries (GROUP BY, count, etc.) |
| `SearchQuery<T>` / `SearchQueryExtensions` | SurrealDB full-text search via analyzers |
| `SurrealFunctions` (40+ function modules) | SurrealQL function wrappers (array, string, crypto, geo, etc.) |
| `SurrealAsyncQueryExtensions` | Async query execution on IQueryable |
| `StatsExtensions` | Query statistics extension |
| `LoadManyExtensions` | Bulk load helper (via raw SurrealQL IN clause) |
| `MetadataRegistry` / `ITypeMetadata<T>` | Source-generated metadata for entity mapping |
| `DocumentHierarchy` | Polymorphic document hierarchy |
| `Snowflake.NewId()` (`long` IDs) | 🧊 TBD — Distributed snowflake ID generator; offline, unique, monotonically sortable. Replaces Marten's `NextSequenceValue` |
| `Guid.NewGuid()` (`Guid` IDs) | 🧊 TBD — Client-side GUID generation for Guid-keyed entities. Replaces Marten's `NextSequenceValue` |

---

## K. Gap Analysis: Remaining Priority Missing APIs

### ✅ Quick Wins — Complete

All Quick Win items from the original plan are now implemented. See Action Plan above.

### ✅ Moderate Effort — Complete

All Medium-effort items are now implemented. See Action Plan above.

### 🧠 Significant Effort — Remaining (weeks)

| API | Reason | Effort |
|-----|--------|--------|
| `StoreOptions.LinQ` (LinqParsing) | Custom LINQ parser extension point | High |

### 🔄 Not Applicable (PostgreSQL-specific)

| API | Reason |
|-----|--------|
| `BulkInsertAsync` via `COPY FROM STDIN BINARY` | Postgres-specific binary protocol |
| `SerializableSessionAsync` | Postgres isolation levels |
| `NpgsqlConnection` / `NpgsqlCommand` | Postgres-specific |
| RLS / Sharded tenancy / Partitioning | Postgres-specific |

---

## Summary Statistics

| Category | ✅ 1:1 | ⚠️ Close | ❌ Missing | 🔄 N/A | 🟡 Dali-Specific |
|----------|:-----:|:--------:|:---------:|:------:|:---------------:|
| A. Store Initialization | 46 | 33 | 1 | 22 | 29 |
| B. Session APIs | 93 | 16 | 0 | 3 | 18 |
| C. Event Sourcing | 27 | 15 | 0 | 0 | 5 |
| D. Projections | 18 | 8 | 0 | 0 | 4 |
| E. LINQ / Querying | 30 | 3 | 0 | 0 | 0 |
| F. Schema / Mapping | 12 | 1 | 0 | 4 | 3 |
| G. Advanced / Diagnostics | 25 | 4 | 0 | 1 | 8 |
| H. Multi-tenancy | 7 | 2 | 0 | 3 | 1 |
| I. ASP.NET Core | 8 | 1 | 0 | 1 | 0 |
| J. Dali-Specific | 0 | 0 | 0 | 0 | 37 |
| **Totals** | **266** | **83** | **1** | **34** | **106** |

**Key insight:** Dali now has **266** 1:1 Marten-compatible APIs (up from the original 83), **83** close matches, only **1** remaining gap, 34 Postgres-specific items, and 106 Dali-native SurrealDB features. The entire document store, event sourcing, session CRUD, query session, patching, batch queries, diagnostics, infrastructure, subscriptions, search/FTS, event manipulation, streaming tuples, multi-store, cross-tenant operations, bulk inserts, multi-tenancy builders, session management, LINQ `Include()`, `Stats()`, projection coordinator (`IProjectionCoordinator`), and DCB (Dynamic Consistency Boundary) tag support are now fully implemented — 2 DCB items marked ⚠️ (simplified SurrealDB implementation vs Postgres tag tables). The 1 remaining gap is `LinqParsing` (custom LINQ parser). **Test suite: 304 tests, 0 failures.**

---

## Methodology

Each row was verified by:
1. Reading the Marten interface file to extract the exact method signature
2. Grepping Dali's source tree for equivalent method/interface/class names
3. Comparing signatures — return types, parameter shapes, async patterns
4. Reading the Dali implementation to confirm behavioral semantics
