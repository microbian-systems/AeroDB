# Test-first security repair plan

| Requirement | Reproduction / control |
|---|---|
| checked read errors | VaultReadFailureRegressionTests: raw error, later statement error, execute, Load and CheckExists; successful/absent controls |
| cancellation | VaultSaveBoundaryRegressionTests: provider and listener cancellation preserve token and suppress AfterCommit; ordinary failure and successful save controls |
| redacted logging | Success, provider exception and failed response canaries cover private RecordId/path, document fields, rendered messages, structured state and exception text. Synthetic values only; retain operation/count/failure signal. |
| atomic expected-version updates | VaultConcurrencyRegressionTests: independent remote stores held after reads; exactly one complete winner, losing sibling absent, separate late failure rolls back full document and sibling. 2/2 passed in isolated SurrealDB 3.2.4 containers. No demonstrated defect and no speculative production change. |
| generator packaging | Real local pack, isolated global packages, source-mapped NuGet-only consumer without ProjectReference. Consumer build passes, materialization test is genuinely RED. Source mapping forces the local artifact and prevents collisions; no publishing or production pin change. |
| distinct commit-outcome-unknown error | User approved contract. VaultCommitOutcomeRegressionTests: implicit/explicit commit crossed with timeout/cancellation are four RED cases; post-commit listener failure is a passing known-commit control. No transport retry or compensating rollback should occur after uncertain commit. |
| don't break existing functionality | Baseline full AeroDB.Tests run and positive controls; compare existing failures separately. Full suite must eventually be green before release. |

First compile and execute new regressions against unchanged production code;
retain valid RED assertions. No GREEN claim until a later reviewed fix. Before
fixing save semantics, preserve the approved unknown-outcome distinction:
pre-commit cancellation is cancellation; an uncertain commit is a fault requiring
reconciliation, not a safely retryable cancellation.

## Approved implementation / conformance continuation

The user approved the repair phase and later confirmed strict database errors
by default with corrected table fixtures. Implementations now have focused
GREEN receipts; see status.md for counts and exact filenames. The existence
check has an additional RED/GREEN row-count correction. Response-error canaries
also assert the exception graph is redacted, not only emitted logs.

Final full-suite comparison must leave only the six documented baseline failures,
with those failures still preventing an overall release-green claim. The
known-commit/post-callback public exception choice remains with the user; no
package publication or Vault adoption before that gate and separate approval.
