---
title: Roadmap
description: Current status and future plans for AeroDB
---

## Current Status — Alpha

**Version:** 0.0.8.1

AeroDB is in active alpha development. The core document store, session management, basic LINQ querying, and event sourcing primitives are functional, but APIs are subject to change. We recommend against production use at this stage.

---

## Short-Term — v0.1.x (Next 2–3 months)

| Area | Goals |
|------|-------|
| **LINQ Provider** | Full `Where` support (compounds, negations, string/path methods), `Select` projections, `OrderBy`/`ThenBy`, pagination (`Take`/`Skip`), aggregate operators (`Count`, `Sum`, `Min`, `Max`, `Average`), `Any`/`All`, `First`/`FirstOrDefault`/`Single` |
| **Query Operators** | `Include` for eager-loading related documents, `Distinct`, `GroupBy` with aggregate projections, `OfType` for polymorphic queries, full-text search integration |
| **Event Store** | Inline projections, async projection daemon, multi-stream projections, event metadata enrichment, event versioning and migration |
| **Stability** | SurrealQL injection fuzzing, connection resilience (automatic reconnect, retry policies), transaction consistency tests |
| **Documentation** | API reference generation, getting-started walkthrough, event sourcing guide, graph query tutorial |

## Medium-Term — v0.5.x (Next 4–6 months)

| Area | Goals |
|------|-------|
| **EF Core Bridge** | GA release of `AeroDB.EntityFrameworkCore` with full `DbContext` compatibility, migration support, hybrid document/relational workloads |
| **Wolverine Integration** | Mature Wolverine saga persistence, inbox/outbox support, durable scheduled messages, transactional outbox to SurrealDB |
| **Bulk Operations** | `BulkInsert`, `BulkUpdate`, `BulkDelete` with batch optimization, progress reporting, and error aggregation |
| **Schema Management** | Patch-based migrations (non-destructive schema evolution), diff-based schema comparison, rollback support |
| **Batch Queries** | `IBatchQuery` for composing multiple queries in a single round-trip, with dependency-aware execution |
| **Graph API** | Recursive graph traversal, shortest-path queries, subgraph extraction, named graph definitions |
| **Vector Search** | Query-time vector index selection, hybrid vector + filter queries, multi-vector per document, score/explain output |

## Long-Term — v1.0 (8+ months)

| Area | Goals |
|------|-------|
| **Production Readiness** | API stability guarantee (semver), breaking-change policy documented, LTS support window defined |
| **Performance** | Published benchmarks against MartenDB and raw SurrealDB, connection pooling optimization, query plan caching, pre-compiled queries |
| **Comprehensive Documentation** | Full API reference, migration guides (from MartenDB, from raw SurrealDB), architecture decision records (ADRs), troubleshooting guides |
| **Testing Infrastructure** | Property-based testing suite, SurrealDB version matrix CI, chaos engineering for connection resilience |
| **Monitoring & Observability** | OpenTelemetry metrics (`db.client.*` semantic conventions), structured logging integration (Serilog sink), health-check endpoints, Prometheus metrics |
| **Tooling** | .NET CLI templates (`dotnet new aerodb`), Visual Studio item templates, schema visualization, SurrealQL EXPLAIN integration for query profiling |

## Community

| Channel | Purpose |
|---------|---------|
| [GitHub Issues](https://github.com/microbian-systems/AeroDB/issues) | Bug reports, feature requests |
| [GitHub Discussions](https://github.com/microbian-systems/AeroDB/discussions) | Q&A, ideas, show & tell |
| [Contributing Guide](/docs/contributing) | How to build, test, and submit PRs |
| [Code of Conduct](https://github.com/microbian-systems/AeroDB/blob/main/CODE_OF_CONDUCT.md) | Community standards |

Contributions are warmly welcomed — whether it's fixing a typo in the docs, adding a missing LINQ operator, or proposing a new feature. See the [Contributing Guide](/docs/contributing) to get started.

---

*This roadmap reflects current priorities and may shift based on community feedback and upstream SurrealDB changes. For the most up-to-date status, check the [GitHub milestones](https://github.com/microbian-systems/AeroDB/milestones).*
