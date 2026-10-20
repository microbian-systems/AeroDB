# Vault security regression phase — 2026-10-11

Tests-only scope on `hotfix/wolverine-sable-outbox`. Preserve existing dirty
Wolverine/cache/schema changes. No commits, publication, pins or live databases.

The proposal assesses published Sable 0.0.9.7-alpha (5a09bd6); current source
must be tested independently. Use TUnit 1.39, Shouldly and NSubstitute.

Targets: InternalSessionBase raw/execute/load/exists; DocumentSession save
cancellation and debug logging; full-document expected-version update; package
consumer generated metadata. Existing RawQueryTests, QuerySessionParityTests,
DocumentSessionWriteLiteralTests and DocumentVersionFenceTests provide controls.

Static Roslyn pairing was run once over src/AeroDB.Sable (291 source files).
That narrow root excludes sibling tests, so its zero pairings do not mean the
repository lacks tests and are not coverage evidence.

Baseline build from ContractorAI root with SDK 10.0.401 succeeded, 7 existing
duplicate-using warnings. Repo pins unavailable SDK 10.0.301; pin unchanged.
Baseline test run before new tests were built completed: 2,179 total, 2,173
passed, 6 failed, 0 skipped. Failures: two queue-sender mock null references,
three existing tenant-cache regressions, and one embedded authentication fixture.
