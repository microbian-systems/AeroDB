# Sable server capability tests

This project executes the 13 public capability scenarios in
`.docs/spec/sable-query-contracts.md` against a real SurrealDB server. It is
intentionally separate from `AeroDB.Tests` and has no embedded-engine package
dependency. DiskANN, live queries, and other server-only behavior belong here.

Start the repository's pinned SurrealDB 3.2 server and run the project with:

```powershell
docker compose up -d
dotnet test --project tests/AeroDB.Sable.Server.Tests/AeroDB.Sable.Server.Tests.csproj
```

The defaults connect to `ws://localhost:8000/rpc`, namespace
`sable_capability`, and one stable database per scenario. Override them with:

- `AERODB_SERVER_TEST_ENDPOINT`
- `AERODB_SERVER_TEST_NAMESPACE`
- `AERODB_SERVER_TEST_USERNAME`
- `AERODB_SERVER_TEST_PASSWORD`

Tests fail with a prerequisite error when the server is unavailable or older
than 3.2. They never silently skip. Bogus data uses a fixed seed per scenario,
and tests run serially so a failed scenario remains inspectable.

## Capability rollout

| Contract | Scenario | Server test | Production prerequisite |
|---|---|---|---|
| SQC-01 | Multi-model query | Pending | RF-030, RF-060, RF-061 |
| SQC-02 | ACID transactions | Active | Existing public API; RF-050-RF-052 will port internals to the AST |
| SQC-03 | Built-in auth | Pending | RF-070 |
| SQC-04 | Hybrid RAG | Pending | RF-060 |
| SQC-05 | Graph RAG | Pending | RF-060, RF-061 |
| SQC-06 | Context expansion | Pending | RF-061 |
| SQC-07 | Knowledge graphs | Pending | RF-052, RF-061 |
| SQC-08 | Agent memory | Pending | RF-060 |
| SQC-09 | Conversational memory | Pending | RF-060 |
| SQC-10 | Live queries | Pending | RF-063 |
| SQC-11 | Recommendations | Pending | RF-060, RF-061 |
| SQC-12 | Geospatial queries | Pending | RF-062 |
| SQC-13 | Event-driven automation | Pending | RF-071 |

An integration test becomes active only when its exact public example compiles
and the implementation exists. The project does not contain ignored tests or
passing placeholders for unfinished contracts.
