# AeroVault prerequisites: RED and implementation checkpoints — 2026-10-11

The first section preserves the original tests-only checkpoint. The subsequent
implementation section records the later user-approved source changes and
receipts. Do not interpret the historical RED section as current GREEN status.

Original RED checkpoint: hotfix/wolverine-sable-outbox. Production source was
untouched during that initial tests-only phase.
No commits, publication, package pins, parent gitlinks, live DB changes or secrets.

## Execution evidence

- Baseline build: exit 0, 7 existing duplicate-using warnings.
- Baseline old suite: 2,179 total, 2,173 passed / 6 failed / 0 skipped, 4m17s.
  `tests/AeroDB.Tests/bin/Debug/net10.0/TestResults/security-baseline.trx`.
- New test build: exit 0, 5 existing duplicate-using warnings, 0 errors.
- RED batch: 14 total, 9 expected failures / 5 passed / 0 skipped.
  `tests/AeroDB.Tests/bin/Debug/net10.0/TestResults/vault-security-red.trx`.
- Positive controls independently rerun clean: read/absence 3/3,
  ordinary-provider-failure 1/1, successful-write/AfterCommit 1/1.

Commands invoked from ContractorAI root using installed SDK 10.0.401 without
changing unavailable nested SDK 10.0.301 pin:

```powershell
dotnet build AeroCMS/AeroDB/tests/AeroDB.Tests/AeroDB.Tests.csproj --no-restore -v quiet
dotnet exec AeroCMS/AeroDB/tests/AeroDB.Tests/bin/Debug/net10.0/AeroDB.Tests.dll --maximum-parallel-tests 4 --timeout 5m --no-progress --disable-logo --report-trx --report-trx-filename security-baseline.trx
dotnet exec AeroCMS/AeroDB/tests/AeroDB.Tests/bin/Debug/net10.0/AeroDB.Tests.dll --treenode-filter "/*/*/Vault*RegressionTests/*" --maximum-parallel-tests 1 --timeout 1m --no-progress --disable-logo --report-trx --report-trx-filename vault-security-red.trx
```

## Assertion and gap review

All 14 tests contain awaited exception assertions or meaningful state/value/
absence assertions. The null-only missing-document control is intentional,
paired with an error-must-throw test; it does not claim general read coverage.
Cancellation tests check token identity, canceled task state, no AfterCommit and
no unexpected write. Logging captures both rendered and structured state and
checks a successful-save signal is retained. No skips or sleeps were added.

Static mutation reasoning only; no production mutations were injected because
this phase is tests-only and baseline is not green. The observed RED run proves
current suppression/wrapping/log leakage; it does not establish a mutation score.

## Expanded tests-first receipts

- Final source test build: 0 errors, 5 existing duplicate-using warnings.
- Final Vault regression batch: 21 total, 15 expected failures / 6 passing
  controls / 0 skipped. `TestResults/vault-security-red-final.trx`.
  Includes four automatic/explicit commit timeout/cancellation cases asserting
  the user-approved distinct unknown-outcome fault, and one passing known-commit
  post-commit listener failure control. Real driver transactions use a substituted
  provider engine; no network or real secrets.
- NuGet-only consumer: local pack and isolated-source restore passed; consumer
  build 0 errors/0 warnings; materializer test 1 expected failure / 0 skips.
  `../AeroDB.PackageConsumer.Tests/bin/Debug/net10.0/TestResults/package-generator-red.trx`.
  Restored `.nupkg.metadata` source points to the local temporary package feed,
  not NuGet.org. Source mapping restricts AeroDB.Sable to that feed; no source
  ProjectReference exists in the consumer. Published package and local pack
  omit analyzer assets. The fixture config/README make the local-source and
  fresh-cache boundary reproducible without a production pin change.
- Remote concurrency proof: build 0 errors/0 warnings; 2/2 passed / 0 skipped
  against test-owned loopback SurrealDB 3.2.4 containers with unique ns/db.
  `../AeroDB.Sable.Server.Tests/bin/Debug/net10.0/TestResults/vault-concurrency-proof.trx`.
  Both complete winning update/loser sibling absence and late-failure rollback
  are proven. No expected-version production repair is justified by these tests.
  Docker inspection confirmed the fixtures were removed; no live demo DB used.
- Aggregate new executed cases: 24; 16 expected failures / 8 passing controls
  or transaction proofs / 0 skipped. These are RED-phase results, not a passing
  release suite. No production fixes performed.
- Independent oracle: ACCEPTED WITH ADVISORIES for tests-only checkpoint;
  requested execution receipts and local-source collision protection now added.
  Permissive empty-response success double remains a documented advisory: that
  specific test proves invocation/AfterCommit, not persisted document contents.

An attempted global fixture PackageVersion override propagated into driver
dependency versions and failed restore (NU1605); reverted test configuration to
the actual package version. The successful final rerun uses explicit local-source
mapping and isolated cache, not a downgrade suppression or source reference.

Not complete: GREEN read/cancellation/logging/generator repairs, additional
supported-platform conformance and full-suite regression clearance. Existing
six baseline failures remain separate. No commits, publication, pins or secret
endpoint activation occurred.

## Implementation checkpoint — 2026-10-11

The user subsequently approved the bounded repairs. Changes remain on
`hotfix/wolverine-sable-outbox`; unrelated Wolverine/schema/cache edits are
preserved. No commit, push, publication, pin change or live database cutover.

- Checked reads now call `EnsureAllOks`; Load no longer swallows database errors
  or caller cancellation. Direct read/query logs omit SQL, identifiers, tenant
  values and parameter values/names.
- Automatic and explicit document commits preserve pre-submit cancellation and
  roll back. Once submitted, timeout, cancellation and IO/HTTP/WebSocket transport
  failures raise `CommitOutcomeUnknownException`, retaining the cause without
  retry, compensating cancel or AfterCommit. Confirmed server rejection remains
  distinct. Write logs exclude SQL/values/error details/raw exception objects;
  safe operation/count/type diagnostics remain.
- NuGet packaging includes exactly `AeroDB.Analyzers.dll` and
  `AeroDB.SourceGenerators.dll` under `analyzers/dotnet/cs`, with no bundled Roslyn
  runtime assemblies. Fresh source-mapped feed/cache consumer restore and build
  passed; generated materializer test **1/1 passed**, no source references.
  Receipt: `package-generator-green.trx` in the package-consumer TestResults.
- Vault focused candidate **33/33 passed**, 0 skipped; receipt
  `vault-security-green.trx`. Six new transport cases are conformance tests
  added after implementation, not claimed as original RED reproductions.
- Server concurrency/rollback rerun **2/2 passed**, 0 skipped; receipt
  `vault-concurrency-green.trx`. Test-owned SurrealDB 3.2.4 containers only.
  Expected-version production behavior remains unchanged.
- The valid-missing-document control was corrected to create its table before
  loading an absent record. Its old empty-store fixture had relied on swallowed
  missing-table errors, not valid absence. Record-ID quoting was already correct.
- Full-suite comparison is in progress; preliminary failures include the known
  Wolverine/cache baseline cases. No full-suite-green claim yet.

Full candidate receipt: `security-full-green-candidate.trx` = 2,212 total,
2,191 passed / 21 failed / 0 skipped, 4m39s. Six are the known baseline; 15
additional failures expose missing-table assumptions. The user explicitly
approved **strict by default; correct table fixtures**. Old valid-absence and
rollback tests now provision their tables before the tested operation, retaining
target null/false/rollback assertions. The graph missing-table test now asserts
an error, paired with a new existing-empty-table null control. Projection rebuild
fixtures provision their target table; no production compatibility shim added.

Two additional defects were reproduced after this comparison:

- `vault-exists-valid-empty-red.trx`: successful empty SELECT incorrectly returns
  true because CheckExists counted OK statements, not rows. Fixed to count CBOR
  records after checking response errors, preserving existing false assertion.
  GREEN `vault-exists-green.trx`: all six existing existence-check cases pass.
- `vault-response-error-graph-red.trx`: response error details appeared in thrown
  exception graphs, even though library logs were clean. Removed raw response
  formatting; response failures are redacted by default. Arbitrary provider
  exception causes remain lossless for trusted callers; Vault must map/redact
  those at its boundary rather than logging the graph.
  GREEN `vault-security-green-final.trx`: 33/33 focused cases pass, including
  the strengthened response-exception graph test and six transport fault cases.

Independent review identified a follow-on public-contract choice: an AfterCommit
callback can fail/cancel after a confirmed commit. Asked the user about a distinct
known-commit/post-processing exception. The existing test asserts only that it
is not an unknown database outcome. Until resolved this remains a release blocker,
not evidence that the operation is safely retryable. Direct automatic event
mutation commits also remain outside this bounded document-commit repair.

Skills used: tests-first implementation, .NET test execution, security hardening,
assertion-quality and gap analysis. Current assertions include exact fields/type,
token/task state, call counts, no retry/cancel/callback, and rendered/structured
log canaries. No coverage or mutation score is claimed. Earlier RED receipts are
preserved; no injected mutation is left in source.

## Final bounded checkpoint

- Final build: exit 0, 0 errors, 13 pre-existing compiler warnings on the
  rebuilt dependency graph (duplicate using, unused Identity fields, CBOR nullable).
- Focused security: **33/33 pass**, `vault-security-green-final.trx`.
- Existence checks: **6/6 pass**, `vault-exists-green.trx`.
- NuGet-only generated materialization: **1/1 pass**, `package-generator-green.trx`.
- Isolated remote concurrency/rollback: **2/2 pass**, `vault-concurrency-green.trx`.
- Full suite: **2,213 executed; 2,207 pass / 6 fail / 0 skipped**, 4m36s,
  `security-full-final.trx`. XML failed-name comparison against the pre-repair
  baseline has no differences. No newly failing cases remain after the approved
  strict-by-default fixture updates; this is not an overall green release suite.
- Docker query by `aerodb.testfixture=expected-version-race` found no remaining
  test fixture containers. Driver and `.gitmodules` diffs are empty; branch
  remains `hotfix/wolverine-sable-outbox`, HEAD `46875ff` (no new commit).
- Independent oracle verdict: **CHANGES REQUIRED** for the known-success
  post-commit callback exception contract only; implementation awaits user choice.
  Required proof: ordinary exception and cancellation, automatic and explicit
  commit, fault indicating commit succeeded, retained cause/token, exactly one
  commit/callback, no cancel/retry. Provider boundary redaction, routing logs and
  direct event commit semantics remain separately documented follow-ups.

### Requirement / evidence handoff

| User requirement | Observed evidence |
|---|---|
| "create unit tests first" | Original RED receipts plus additional valid-existence and response-exception RED receipts; fixes subsequently pass. |
| "make sure we don't break any existing functionality" | Final full failed-name set equals baseline six; strict missing-table behavior is an explicitly approved change, not silently preserved legacy behavior. |
| "Distinct unknown-outcome error" | Automatic/explicit timeout, cancellation and IO/HTTP/WebSocket tests fault distinctly, retain cause, no retry/cancel/AfterCommit. |
| "Strict by default; correct table fixtures" | Error propagation retained; old valid-absence/rollback assertions retained with provisioned tables; graph error vs successful empty-table controls. |

No commits, push, release, pins, live database cutover, Vault secret endpoints,
paid AI activation or MVP-readiness claim. Await the known-commit API choice,
then reverify before any separate package adoption/release authorization.
