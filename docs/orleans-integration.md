# Dali.Orleans — Grain Persistence for Microsoft Orleans

> **Status:** 📋 Planned
> **Priority:** Post-v1.0
> **Estimate:** ~200 lines, 5-8 files

## Overview

A `Dali.Orleans` package providing SurrealDB-backed grain state persistence for
[Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/) via Dali's
`IDocumentStore`. This is the **storage adapter** layer — Orleans handles
messaging, clustering, and compute; Dali.Orleans gives grains a durable,
SurrealDB-backed persistence target.

## Key Design Insight — Significantly Simpler than Wolverine

Orleans ships with its own networking, scheduling, streaming, and clustering.
The integration only needs to implement one core Orleans abstraction:

| Abstraction | Role | Required? |
|-------------|------|-----------|
| `IGrainStorage` | Read/write/clear grain state | ✅ Yes (core) |
| `ILogConsistentGrain` | Event-sourced journaled grains | 🔶 Optional |

This yields ~5-8 files vs Wolverine's 36 files (which needed transport,
outbox/inbox, saga storage, code-gen frames, subscription bridges, scheduled
job agents, and health checks).

This is **supplementary** to Wolverine, not a replacement:

| Capability | WolverineFx | Orleans |
|------------|-------------|---------|
| Cross-service messaging | ✅ Full (outbox, inbox, transport) | ❌ Not relevant |
| Sagas / durable workflows | ✅ Native | ❌ Uses timers |
| Virtual actors / grains | ❌ Not relevant | ✅ Core model |
| State persistence | Through saga storage only | ✅ `IGrainStorage` (this integration) |

## Package Structure

```
src/Dali.Orleans/
  Dali.Orleans.csproj
  DaliGrainStorage.cs               # IGrainStorage implementation
  DaliGrainStorageOptions.cs         # Options class
  DaliGrainStorageFactory.cs         # Factory for Orleans creation path
  DaliOrleansServiceExtensions.cs    # AddDaliGrainStorage() DI extensions
  DaliOrleansConfigurator.cs         # IConfigureDali — auto-create tables
  (optional)
  DaliEventStorageProvider.cs        # IEventStorageProvider for JournaledGrain
  DaliJournaledGrainOptions.cs       # Options for journaled grain storage
```

## Core API

### User-Facing Registration

```csharp
// Program.cs — standard Orleans silo configuration
using var host = await new HostBuilder()
    .UseOrleans(silo =>
    {
        // Reuse existing Dali store (already configured via AddDali)
        silo.AddDaliGrainStorage("dali", options =>
        {
            options.Store = sp.GetRequiredService<IDocumentStore>();
        });

        // Or let Dali.Orleans configure its own isolated store
        silo.AddDaliGrainStorage("dali", options =>
        {
            options.Endpoint = "http://localhost:8000";
            options.Namespace = "myapp";
            options.Database = "orleans";
        });
    })
    .Build();
```

### Grain Usage

```csharp
// Grain with persistent state — standard Orleans pattern
public sealed class UserGrain : Grain, IUserGrain
{
    private readonly IPersistentState<UserProfile> _profile;

    public UserGrain(
        [PersistentState("profile", "dali")]
        IPersistentState<UserProfile> profile)
    {
        _profile = profile;
    }

    public async Task UpdateProfile(UserProfile p)
    {
        _profile.State = p;
        await _profile.WriteStateAsync();
    }

    public Task<UserProfile> GetProfile() => Task.FromResult(_profile.State);
}
```

### Multiple Named Storage Providers

```csharp
// Different grain types can use different storage backends
silo.AddDaliGrainStorage("dali-users", opts => opts.Store = usersStore);
silo.AddDaliGrainStorage("dali-orders", opts => opts.Store = ordersStore);
silo.AddDaliGrainStorage("dali-default");  // uses IDocumentStore from DI

// Grain resolves by name
public sealed class OrderGrain : Grain, IOrderGrain
{
    public OrderGrain(
        [PersistentState("state", "dali-orders")]
        IPersistentState<OrderState> state) { }
}
```

## DaliGrainStorage — IGrainStorage Implementation

```csharp
public sealed class DaliGrainStorage : IGrainStorage
{
    private readonly IDocumentStore _store;

    public DaliGrainStorage(IDocumentStore store)
        => _store = store;

    public async Task ReadStateAsync<T>(
        string grainType, GrainId grainId, IGrainState<T> grainState, CancellationToken ct)
    {
        await using var session = _store.QuerySession();
        var key = grainId.ToString();
        var existing = await session.LoadAsync<T>(key, ct);

        if (existing is not null)
        {
            grainState.State = existing;
            grainState.RecordExists = true;
        }
    }

    public async Task WriteStateAsync<T>(
        string grainType, GrainId grainId, IGrainState<T> grainState, CancellationToken ct)
    {
        await using var session = _store.LightweightSession();
        var key = grainId.ToString();
        session.Store(grainState.State, key);
        await session.SaveChangesAsync(ct);
    }

    public async Task ClearStateAsync<T>(
        string grainType, GrainId grainId, IGrainState<T> grainState, CancellationToken ct)
    {
        await using var session = _store.LightweightSession();
        var key = grainId.ToString();
        session.Delete<T>(key);
        await session.SaveChangesAsync(ct);
    }
}
```

## State Storage Model

| Concern | Strategy |
|---------|----------|
| **Table name** | `orleans_grainstate_{grain_type}` — auto-created via `IConfigureDali` |
| **Document key** | `GrainId.ToString()` — Orleans' canonical grain identifier |
| **Serialization** | SurrealDB CBOR (same as all Dali documents) |
| **Metadata** | Full Dali metadata (versioning, timestamps, soft delete) |
| **Tenancy** | Inherits Dali's `TenancyStyle` — grain state can be tenant-aware |

## Optional: JournaledGrain / Event Storage

Orleans `JournaledGrain<TState, TEvent>` uses `IEventStorageProvider` for
event-sourced grains. A `DaliEventStorageProvider` would bridge Dali's
`IEventStore` to Orleans' journaled grain abstraction:

```csharp
silo.AddDaliGrainStorage("dali-journaled", opts =>
{
    opts.UseEventSourcing = true;
    opts.EventStore = myStore;
});
```

This is **deferred** — the basic `IGrainStorage` covers 90%+ of use cases.

## Testing Strategy

### Unit Tests (Phase 1 — Mandatory)
- Orleans provides `InProcessSilo` and `TestCluster` for in-memory cluster testing
- Use `SurrealDbMemoryClient` (Dali's embedded engine) — no external SurrealDB needed
- Test grain state lifecycle: read, write, clear, overwrite, nonexistent grain
- Test multiple named storage providers
- Test concurrent grain state access
- Test error recovery (simulated store failures)

### Integration Tests (Phase 2 — Deferred)
- Require a live Orleans cluster + SurrealDB instance
- Test multi-silo grain state consistency
- Test serialization round-trips with complex states
- Test cluster reconnection and failover

## Project Setup

### New Project: `src/Dali.Orleans/`

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <Description>SurrealDB-backed grain state persistence for Microsoft Orleans</Description>
        <PackageId>Dali.Orleans</PackageId>
        <PackageTags>orleans surrealdb grain persistence actors</PackageTags>
    </PropertyGroup>
    <ItemGroup>
        <ProjectReference Include="..\Dali\Dali.csproj" />
    </ItemGroup>
    <ItemGroup>
        <PackageReference Include="Microsoft.Orleans.Runtime" />
        <PackageReference Include="Microsoft.Orleans.Core" />
    </ItemGroup>
</Project>
```

### Registration with NuGet Packaging

Add `"$RepoRoot/src/Dali.Orleans"` to the `$libProjects` array in:

- `build/nuget-pack.ps1` (line ~72-78)

This ensures `Dali.Orleans` is included in all NuGet pack/publish operations
(preview and release) automatically.

## Implementation Order

| Step | What | Est. |
|------|------|------|
| 1 | Create `src/Dali.Orleans/` project + csproj | 10 min |
| 2 | Implement `DaliGrainStorage` (IGrainStorage) | 30 min |
| 3 | Add `DaliGrainStorageOptions` + Factory | 15 min |
| 4 | Add `DaliOrleansServiceExtensions` | 15 min |
| 5 | Add `DaliOrleansConfigurator` (IConfigureDali) | 15 min |
| 6 | Add `Dali.Orleans` to `nuget-pack.ps1` | 2 min |
| 7 | Unit tests (Orleans InProcessSilo + embedded engine) | 2-3 hr |
| **Total** | | **~4 hr** |

Integration testing (live Orleans cluster) is deferred to a later phase.
