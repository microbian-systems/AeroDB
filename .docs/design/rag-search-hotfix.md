# RAG search query hotfix

Branch: `hotfix/rag-search`. Initially implemented on `feature/refactor` at
`3d9fc2b`, then aligned with `origin/develop` for publication. Develop already
contains the equivalent identity-ordering fix (`dd32024`), which is retained
rather than replaced by this hotfix. The PR adds the hybrid-search changes,
regression tests and this report, not an additional identity implementation.
Initial verification: 2026-10-13 with .NET SDK 10.0.401 and SurrealDB 3.3.0.
No changes were made to the `surrealdb.net` submodule; its pre-existing
checkout difference was preserved and is excluded from the hotfix commit.

## Causes and fixes

| Failure | Cause | Fix |
|---|---|---|
| Hybrid search returns no records | LET/LET/RETURN has three response slots; the reader used slot 0 (NONE) | Read the RETURN at slot 2 |
| Wrong fusion scores/limits | Generated `search::rrf(lists, k, candidateLimit)` | Use `search::rrf(lists, fusedLimit, k)` |
| Take ignored; Skip changes ranking | Default path omitted the final limit; paged results ordered by text score | Always apply Take/Skip, default to `rrf_score DESC`, retain enough fused rows |
| Custom ordering distorts fusion | User order replaced relevance ranking in the text candidate query | Keep inputs relevance-ranked; apply ordering to the complete bounded candidate union |
| Database errors look like no matches | Reader discarded responses with errors | Throw with server diagnostics; valid empty searches still return an empty list |
| Identity sorting rejected by parser | Identity key-extraction/cast expressions were placed inside ORDER BY | Order by native `id`; retain CLR key extraction for predicates/projections |

The documented RRF signature is
[`search::rrf(lists, limit, k)`](https://github.com/surrealdb/docs.surrealdb.com/blob/main/src/content/reference/query-language/functions/database-functions/search.mdx).
Native record ids within one table sort by their typed key, preserving numeric,
string and UUID identity semantics without adding synthetic projection fields.

## Verification

The initial five reproduction cases failed before editing production code and
passed afterward. The final server test project passed **17/17**, zero skipped:
11 new RAG regression cases plus six existing transaction/identity cases.
The complete AeroDB test suite was not run.
These initial results precede alignment with current develop; publication
verification is recorded separately below.

| Requirement | Evidence in `RagSearchRegressionTests` |
|---|---|
| Materialization, argument order, Take | `HybridReturnsFusedRecordsAndHonorsTake` checks IDs and the exact RRF score with k=1 |
| Rank-preserving Skip | `HybridPaginationPreservesFusionRank` compares a page with the unpaged ranked result |
| Custom ordering | `HybridOrderingSeesFullCandidateUnion` fuses distinct top text/vector candidates |
| Bound filters | `HybridFiltersBothCandidateBranches` excludes a record from both candidate lists |
| Honest error behavior | `SearchErrorsAreNotReportedAsEmptyResults`, `EmptySearchRemainsAnEmptyResult` |
| Identity paging | `NumericIdentityPaginationUsesNativeRecordKey` (both directions), `StringIdentityPaginationUsesLexicalRecordKey` |
| Guid and mapped identities | `ConventionalGuidIdentityOrderingUsesNativeRecordKey`, `MappedIdentityThenByTranslatesToNativeRecordKey` |

Search fixtures use unique databases to isolate index state across repeated
runs. A transient transaction cancellation was observed while repeatedly
deleting/repopulating an indexed fixture; no retry or exception suppression
was added. Test data is retained under an isolated test namespace for inspection.

The SurrealDB MCP parser reproduced the illegal ORDER BY function and accepted
native `id` ordering. Its bundled engine does not expose `search::rrf`, and
its loopback cannot reach the Windows server. Fusion and storage behavior were
therefore tested through the real SDK against the local Windows server.

Run from the sibling EQ checkout (whose SDK selection allows 10.0.401; AeroDB
currently pins 10.0.301 in its `global.json`):

```powershell
$env:AERODB_SERVER_TEST_ENDPOINT = 'ws://127.0.0.1:18768/rpc'
$env:AERODB_SERVER_TEST_NAMESPACE = 'sable_rag_hotfix'
dotnet run --project ../AeroDB/tests/AeroDB.Sable.Server.Tests -- --report-trx
```

EQ references this checkout's Sable project instead of the affected NuGet
release. Its 15 focused/integration checks passed with real MiniLM ONNX vectors,
and a fluent hybrid query returned two cited chunks from the existing `ash.pdf`
dataset for a limit of two. No corpus reingestion was needed.

## Publication verification

After aligning the hotfix with develop at `5be886f`, all **17/17** server tests
passed again with zero skips against SurrealDB 3.3.0. This run used a clean,
isolated checkout with develop's exact pinned driver commit
`ee62b57972e7d4624e3d3362679a03ee794d80c0`, rather than the unrelated existing
driver checkout. The original driver HEAD and files were left unchanged.

At initial publication, the diff against develop contained only SearchQuery.cs, the RAG regression
test file, its test-project README and this report. The existing identity-ordering
implementation and vector-scope predicate fix from develop are preserved.
That was focused server verification. The later CI/native-library and
full-suite work is recorded in [ci-and-full-suite-verification.md](ci-and-full-suite-verification.md),
including the remaining upstream live-notification blocker. PR #8 is not yet merge-ready.

## Separate known limitation

SchemaManager.GetFieldSchemas excludes only the conventional property `Id`,
not a configured alternate identity such as `Key`. Schema generation defines
`key TYPE int`, while POCO writes omit it because identity belongs to the native
record key. SaveChanges then fails with `Expected int but found NONE`.

This pre-existing schema-generation issue is not changed by this query hotfix.
The mapped-identity regression verifies translation only, independent of that
storage path. Conventional string, numeric and UUID identities are exercised
through real storage and retrieval. EQ uses conventional `Id` properties.
