# Testing Plan

## Goal
90%+ coverage of all **public APIs** (public classes, interfaces, methods) across all Dali projects.

## Coverage Targets

| Project | Public Members | Tested | Coverage | Status |
|---------|---------------|--------|----------|--------|
| Dali | ~150 | ~120 | ~72% line | Needs ~30 more public method tests |
| Dali.WolverineFx | ~90 | ~75 | ~36% line* | Needs DaliTransport + DaliEndpoint |
| Dali.AspNetIdentity | 73 | 72 | ~99% | Needs AddDaliStores extension (1 test) |
| Dali.Snowflake | 2 | 2 | 100% | Done |
| Dali.SourceGenerators | 3 (public entry points) | 3 | 93.7% | Done |

*\* WolverineFx line coverage is low because internal classes (DaliScheduledJobAgent, SubscriptionHostedService, codegen) are unreported; public API coverage is much higher.*

---

## Dali.AspNetIdentity — Public API Inventory

### DaliUserStore`<TUser, TRole>` (62 public methods)
All 62 methods tested across 7 test files (125 total tests).

| Test File | Methods Covered |
|-----------|----------------|
| `DaliUserStoreCoreTests.cs` | CreateAsync, UpdateAsync, DeleteAsync, FindByIdAsync, FindByNameAsync, GetUserIdAsync, GetUserNameAsync, SetUserNameAsync, GetNormalizedUserNameAsync, SetNormalizedUserNameAsync, GetPasswordHashAsync, SetPasswordHashAsync, HasPasswordAsync, GetEmailAsync, SetEmailAsync, GetEmailConfirmedAsync, SetEmailConfirmedAsync, FindByEmailAsync, GetNormalizedEmailAsync, SetNormalizedEmailAsync, GetPhoneNumberAsync, SetPhoneNumberAsync, GetPhoneNumberConfirmedAsync, SetPhoneNumberConfirmedAsync, GetTwoFactorEnabledAsync, SetTwoFactorEnabledAsync, GetSecurityStampAsync, SetSecurityStampAsync, GetLockoutEndDateAsync, SetLockoutEndDateAsync, IncrementAccessFailedCountAsync, ResetAccessFailedCountAsync, GetAccessFailedCountAsync, GetLockoutEnabledAsync, SetLockoutEnabledAsync |
| `DaliUserStoreAuthTests.cs` | GetAuthenticatorKeyAsync, SetAuthenticatorKeyAsync, RedeemCodeAsync, CountCodesAsync, ReplaceCodesAsync, SetTokenAsync, RemoveTokenAsync, GetTokenAsync |
| `DaliUserStoreClaimTests.cs` | GetClaimsAsync, AddClaimsAsync, ReplaceClaimAsync, RemoveClaimsAsync, GetUsersForClaimAsync |
| `DaliUserStoreLoginTests.cs` | AddLoginAsync, RemoveLoginAsync, GetLoginsAsync, FindByLoginAsync |
| `DaliUserStorePasskeyTests.cs` | GetPasskeysAsync, AddOrUpdatePasskeyAsync, FindPasskeyAsync, FindByPasskeyIdAsync, RemovePasskeyAsync |
| `DaliUserStoreRoleTests.cs` | AddToRoleAsync, RemoveFromRoleAsync, GetRolesAsync, IsInRoleAsync, GetUsersInRoleAsync |

### DaliRoleStore`<TRole>` (10 public methods)
All 10 methods tested in `DaliRoleStoreTests.cs`.

### DaliIdentityExtensions (1 extension method)
| Method | Status |
|--------|--------|
| `AddDaliStores<TUser, TRole>()` | **NOT TESTED** ❌ |

### Gap: AddDaliStores Extension (Critical → 1 test needed)
The `ServiceCollection.AddDaliStores<TUser, TRole>()` extension registers `DaliUserStore` and `DaliRoleStore` into DI. A single test verifying:
- Registration adds both stores to the service collection
- Resolved services are correct types

---

## Dali.WolverineFx — Public API Inventory

### Public Types (16 public types with ~85 public members)

| Public Type | Members | Dedicated Test | Status |
|------------|---------|---------------|--------|
| `IDaliOp` (interface) | 1 method | `DaliOpsTests.cs` | ✅ |
| `DaliOps` (static) | 3 factory methods | `DaliOpsTests.cs` | ✅ |
| `DaliEnvelope` | 22 props + 3 static/instance methods | `DaliEnvelopeTests.cs` | ✅ |
| `DaliIntegration` | 1 method (Configure) | `DaliIntegrationRegistrationTests.cs` | ✅ |
| `DaliMessageStore` | ~55 public methods | `DaliMessageStoreTests.cs` | ✅ |
| `DaliOutboxedSessionFactory` | 3 methods | `DaliOutboxedSessionFactoryTests.cs` | ✅ |
| `DaliQueueListener` | 6 methods | (tested via integration) | ✅ |
| `DaliQueueSender` | 3 methods | `DaliQueueSenderTests.cs` | ✅ |
| `DaliSagaStorage<TId,TSaga>` | 6 methods | `DaliSagaStorageTests.cs` | ✅ |
| `DaliTransportOptions` | 3 properties | `DaliTransportOptionsTests.cs` | ✅ |
| `ScopedDocumentSessionHolder` | 1 property | (trivial, tested via DaliOutboxedSessionFactory) | ✅ |
| `WolverineEnvelopeSchemas` | 1 method (Configure) | (tested via integration) | ✅ |
| `DaliWolverineOptionsEventExtensions` | 1 extension | `WolverineOptionsExtensionsTests.cs` | ✅ |
| `WolverineOptionsDaliExtensions` | 2 extensions | `WolverineOptionsExtensionsTests.cs` | ✅ |
| `WolverineOptionsSubscriptionExtensions` | 2 extensions | `DaliSubscriptionTests.cs` | ✅ |
| **`DaliTransport`** | **8 members** | **NOT TESTED** ❌ |
| **`DaliEndpoint`** | **4 members** | **NOT TESTED** ❌ |

### Gap: DaliTransport (12 public members across 2 types)

**`DaliTransport`** — 8 public members:
- `string Protocol { get; }`
- `string Name { get; }`
- `Endpoint? ReplyEndpoint()`
- `Endpoint GetOrCreateEndpoint(Uri uri)`
- `Endpoint? TryGetEndpoint(Uri uri)`
- `IEnumerable<Endpoint> Endpoints()`
- `ValueTask InitializeAsync(IWolverineRuntime runtime)`
- `bool TryBuildBrokerUsage(out BrokerDescription description)`

**`DaliEndpoint`** — 4 public members:
- Constructor `DaliEndpoint(Uri uri)`
- `override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)`
- `protected override ISender CreateSender(IWolverineRuntime runtime)`
- `protected override bool supportsMode(EndpointMode mode)`

### WolverineFx Priority
1. `DaliTransportTests.cs` — test all 8 members (highest value, foundational transport)
2. `DaliEndpointTests.cs` — test 4 members (returns listener + sender types)

---

## Existing Test Counts (Baseline)

| Test Project | Total Tests | Pass | Fail |
|-------------|------------|------|------|
| Dali.Tests | 1623 | 1621 | 2 (pre-existing flaky daemon) |
| Dali.AspNetIdentity.Tests | 125 | 125 | 0 |
| **Total** | **1748** | **1746** | **2** |

---

## Implementation Order

1. **AspNetIdentity**: `ExtensionsTests.cs` — 1 test for `AddDaliStores`
2. **WolverineFx**: `DaliTransportTests.cs` — 8 tests
3. **WolverineFx**: `DaliEndpointTests.cs` — 4 tests
