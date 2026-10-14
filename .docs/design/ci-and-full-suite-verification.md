# Native CI prerequisites and full-suite verification

Verification date: 2026-10-14. Branch: `hotfix/rag-search`, PR #8 into `develop`.

## Native libraries

The missing files belong to SurrealDB's embedded .NET drivers, not ONNX,
Godot, BeepBop, MAF or LLM Tornado:

- `SurrealDb.Embedded.InMemory`: `libsurreal_memory.so` on Linux,
  `surreal_memory.dll` and its PDB on Windows.
- `SurrealDb.Embedded.SurrealKv`: `libsurreal_surrealkv.so` on Linux,
  `surreal_surrealkv.dll` and its PDB on Windows.

The pinned driver references the Cargo **debug** output directory even when
the .NET solution is built in Release. CI now builds those two Rust packages
with the driver's required Rust 1.94.0 and verifies the libraries exist before
the .NET build. Successful native output is cached before running .NET tests.
No RocksDB engine or changes to vendor code/gitlinks are needed.

`build/test-all.ps1` discovers every active project under `tests`, runs all
seven even when an earlier suite fails, retains separate TRX/HTML reports,
and rejects missing reports or zero executed tests. CI supplies a loopback,
unauthenticated, ephemeral database and tests both SurrealDB 3.2.5 and 3.3.0.
Neither version is allowed to fail silently.

## Fixes exposed by running all suites

- Recognize Linux's `already locked by another process` SurrealKV diagnostic
  in the existing bounded lock-retry policy. Both Linux configuration suites
  subsequently passed 10/10.
- Fail integration tests when their endpoint is unavailable; the previous
  guards silently returned and were counted as successful tests.
- Isolate integration namespaces, serialize catalog setup, define the live
  table before subscribing, and use the correct table/field/record-ID names.
- Remove a race in the callback-isolation assertion: verify both ordered
  callbacks after they finish, instead of inspecting a shared value that the
  next callback can already have overwritten.
- Link live-query producer cancellation to its internal shutdown token, kill
  SDK subscriptions before completing the output, and drain callbacks.
- Make live-query sessions own and dispose their subscriptions and SDK session.
  Session disposal was previously a no-op, preventing aggregate observables
  from completing.
- Compile the Identity sample's TypeScript before static-asset discovery so
  repeated clean builds can recreate the outputs deleted by Clean.
- Use an invalid vector dimension for the search-error regression. SurrealDB
  3.2.5 can return an empty result for an unindexed identity match where 3.3.0
  errors, so that former fixture did not reliably produce a database error.

These focused lifecycle regressions failed before their fixes and passed after:

- `StopAsync_cancels_an_idle_source_without_caller_cancellation`
- `DisposeAsync_delivers_close_before_completing`
- `DisposeSession_completes_owned_subscriptions`

The complete `LiveQueryTests` class also passed 29/29 after the initial
shutdown fix. The final full non-incremental solution build passed with
0 errors; existing warnings remain.

## Full-suite results and merge gate

Windows validation used .NET SDK 10.0.401, SurrealDB 3.3.0, and develop's
exact driver commit `ee62b57972e7d4624e3d3362679a03ee794d80c0` in an isolated
checkout. The user's original driver checkout and files were preserved.

| Suite | Windows 3.3.0 passed/failed | Linux 3.3.0 passed/failed |
|---|---:|---:|
| AeroDB.Analyzers.Tests | 27/0 | 27/0 |
| AeroDB.AspNetIdentity.Tests | 137/0 | 137/0 |
| AeroDB.IntegrationTests | 4/12 | 4/12 |
| AeroDB.Sable.Configuration.Tests | 10/0 | 10/0 |
| AeroDB.Sable.FSharp.Tests | 9/0 | 9/0 |
| AeroDB.Sable.Server.Tests | 19/0 | 19/0 |
| AeroDB.Tests | 2121/0 | 2121/0 |

All seven suites executed, with **zero skipped**. Totals: **2327 passed,
12 failed**. The workflow correctly exits nonzero; these are not all-green
results. The sequential Windows run and Linux 3.3.0 run agree. One intermediate
Windows run was invalidated by rebuilding its binaries during execution and
is explicitly excluded from verification evidence.

The [Linux comparison run](https://github.com/microbian-systems/AeroDB/actions/runs/38513908981)
also found the same 12 reactive failures on 3.2.5. Its only additional failure
was the version-dependent search-error fixture described above; the corrected
fixture passed in a subsequent local 19/19 server run and requires CI recheck
on 3.2.5.

## Remaining upstream blocker

All 12 remaining failures concern live CREATE/UPDATE/DELETE notifications
after Sable's RPC-managed transactions commit. Connectivity, schema setup,
ordinary persistence, error reporting, and lifecycle completion are no longer
the failing prerequisites.

A minimal reproduction using only the pinned .NET driver, separate observer
and writer connections, and the same table distinguishes the paths:

| Write path | Live CREATE notification on 3.3.0 |
|---|---|
| Ordinary auto-committed query | Received |
| Single query containing `BEGIN; CREATE ...; COMMIT;` | Received |
| RPC `begin`, query with its transaction ID, RPC `commit` | Not received |

The server's [3.3.0 RPC commit handler](https://github.com/surrealdb/surrealdb/blob/v3.3.0/surrealdb/server/src/rpc/websocket.rs)
commits directly with `tx.commit()`. The runtime reproduction establishes
that the missing notifications occur below Sable, not in its query builder.
The same failures on the supported 3.2.5 server rule out fixing this merely
by changing the CI server version.

PR #8 remains **unmerged and blocked**. No tests were skipped, no transaction
was disabled, and no SDK/submodule code was edited to obtain a green build.
Finishing the remaining failures requires a separate server-side hotfix or an
explicitly approved redesign of Sable's transaction execution; replacing
atomic saves with non-transactional writes is not an acceptable workaround.
