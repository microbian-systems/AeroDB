# Rename Plan: Dali → AeroDB (`.docs/` sweep)

## Background

The source code has been renamed from "Dali" to "AeroDB" — all project names, API identifiers, and source generator classes use `AeroDB.*` now. The `.docs/` directory (design, plan, spec) still uses "Dali" pervasively and needs to be updated to match.

**Verified in source (`src/`):**
- `AddDali()` → `AddAeroDB()`
- `IDaliAdvanced` → `IAeroDBAdvanced`
- `IDaliTransaction` → `IAeroDBTransaction`
- `IConfigureDali` → `IConfigureAeroDB`
- `DaliDocumentGenerator` → `AeroDBDocumentGenerator`
- `DaliConfiguratorGenerator` → `AeroDBConfiguratorGenerator`
- `DaliDaemonHealthCheck` → `AeroDBDaemonHealthCheck`
- `src/Dali/` → `src/AeroDB/`
- Namespace `Dali.*` → `AeroDB.*`
- `Documents.For()` — **not renamed**, still correct as-is

---

## API Rename Mapping

Use this table for all find-and-replace operations:

| Old (doc) | New (code) |
|---|---|
| `Dali` (project name in prose) | `AeroDB` |
| `Dali.EntityFrameworkCore` | `AeroDB.EntityFrameworkCore` |
| `Dali.WolverineFx` | `AeroDB.WolverineFx` |
| `Dali.Tests` | `AeroDB.Tests` |
| `Dali.Reactive` | `AeroDB.Reactive` |
| `Dali.ML` | `AeroDB.ML` |
| `Dali.Snowflake` | `AeroDB.Snowflake` |
| `Dali.SourceGenerators` | `AeroDB.SourceGenerators` |
| `Dali.AspNetIdentity` | `AeroDB.AspNetIdentity` |
| `AddDali()` | `AddAeroDB()` |
| `AddDaliStore<T>()` | `AddAeroDBStore<T>()` |
| `AddDaliCheck()` | `AddAeroDBCheck()` |
| `IDaliAdvanced` | `IAeroDBAdvanced` |
| `IDaliTransaction` | `IAeroDBTransaction` |
| `IDaliSession` | `IAeroDBSession` |
| `IConfigureDali` | `IConfigureAeroDB` |
| `DaliDaemonHealthCheck` | `AeroDBDaemonHealthCheck` |
| `DaliDocumentGenerator` | `AeroDBDocumentGenerator` |
| `DaliConfiguratorGenerator` | `AeroDBConfiguratorGenerator` |
| `src/Dali/` | `src/AeroDB/` |

---

## File-by-File Plan

### 1. `design/architecture.md` — Heavy (~1467 lines, ~100+ Dali refs)

This is the largest and most impactful doc.

**Changes:**
- Title: `# Dali — Marten-style Document Database` → `# AeroDB — Marten-style Document Database`
- Vision sentence: "Dali is a pragmatic .NET document database" → "AeroDB is a..."
- All project name references (`Dali`, `Dali.EntityFrameworkCore`, etc.) using mapping table
- All API references (`AddDali()`, `IDaliAdvanced`, `DaliDaemonHealthCheck`, etc.)
- All file paths: `src/Dali/` → `src/AeroDB/`
- All prose: "Dali's approach" → "AeroDB's approach", "Dali now uses" → "AeroDB now uses"

**Keep as-is:** `Documents.For()`, Marten comparisons, SurrealDB-specific identifiers.

---

### 2. `design/dali-vs-marten-comparison.md` — Medium (~250 lines, ~70 Dali refs)

**Changes:**
- **Rename file** to `design/aerodb-vs-marten-comparison.md` (if cross-references exist, update them)
- Title: `# Dali vs Marten` → `# AeroDB vs Marten`
- Column headers: "Dali Status" / "Dali API" → "AeroDB Status" / "AeroDB API"
- All API references per mapping table
- All prose descriptions: "Dali is session-level" → "AeroDB is session-level"

**Keep as-is:** Marten column, feature names, `Documents.For()`.

---

### 3. `design/gaps.md` — Heavy (~741 lines, ~80 Dali refs)

**Changes:**
- Title: `# Marten vs Dali — API Gap Analysis` → `# Marten vs AeroDB — API Gap Analysis`
- Prose: "Dali has simpler" → "AeroDB has simpler", "Dali advantage" → "AeroDB advantage"
- API references per mapping table
- File paths: `src/Dali/` → `src/AeroDB/`

---

### 4. `spec/api-parity.md` — Very Heavy (~825 lines, ~200 Dali refs)

This is the most Dali-dense doc — nearly every row references "Dali" in API names or descriptions.

**Changes:**
- Title: `# Dali ↔ Marten Public API Parity` → `# AeroDB ↔ Marten Public API Parity`
- Every API reference per mapping table (~150 row changes)
- Column descriptions: "Dali Equivalent" → "AeroDB Equivalent", "No Dali equivalent" → "No AeroDB equivalent"
- "Dali-native" → "AeroDB-native", "Dali advantage" → "AeroDB advantage"
- File paths: `src/Dali/` → `src/AeroDB/`

---

### 5. `plan/init-impl-plan.md` — Very Heavy (~1231 lines, ~200 Dali refs)

**Changes:**
- Title: `# Dali — Implementation Plan` → `# AeroDB — Implementation Plan`
- All project names per mapping table
- All API references per mapping table
- Phase descriptions: "Dali has simpler" → "AeroDB has simpler"
- File paths: `src/Dali/` → `src/AeroDB/`
- Source generator references

---

### 6. Remaining Plan Docs — Light

| File | What to change |
|---|---|
| `plan/identity-fixes.md` | `Dali.AspNetIdentity` → `AeroDB.AspNetIdentity`, `src/Dali/` paths |
| `plan/marten-samples-port.md` | Project name, `AddDali()` → `AddAeroDB()` |
| `plan/orleans-integration.md` | Project name references |
| `plan/publishing.md` | Package name, NuGet references |
| `plan/schema-options-todo.md` | API names |
| `plan/testing-plan.md` | `Dali.Tests` → `AeroDB.Tests` |
| `plan/wolverine-testing.md` | `Dali.WolverineFx` → `AeroDB.WolverineFx` |
| `plan/documentation-pipeline.md` | Project name in URLs and prose |

---

### 7. Remaining Spec Docs — Light

| File | What to change |
|---|---|
| `spec/aspnet-identity.md` | `Dali.AspNetIdentity` → `AeroDB.AspNetIdentity` |
| `spec/live-query-impl.md` | API names per mapping table |
| `spec/reactive.md` | `Dali.Reactive` → `AeroDB.Reactive` |
| `spec/record-links-spec.md` | File paths (`src/Dali/`) |
| `spec/poco-support.md` | Project name references |
| `spec/surrealdb-net-recordid-strategy.md` | Project name |
| `spec/testing-gaps.md` | Project name |
| `spec/view-support-spec.md` | API names |
| `spec/bulk-update.md` | API names |
| `spec/entity-feature-matrix.md` | Project name |
| `spec/beta-readiness.md` | Project and API names |

---

### 8. File Renames

| Old path | New path | Cross-ref impact |
|---|---|---|
| `design/dali-vs-marten-comparison.md` | `design/aerodb-vs-marten-comparison.md` | Check for links in architecture.md, gaps.md, api-parity.md |

---

## Rules

| Rule | Action |
|---|---|
| `Documents.For()` | **Do NOT replace** — still the correct API |
| Marten references | **Do NOT replace** — separate product being compared |
| SurrealDB identifiers | **Do NOT replace** — e.g., SurrealQL functions, SurrealDb.Net |
| Case sensitivity | Match exact casing: `Dali` not `dali` |
| Prose "Dali" in narrative | Replace with "AeroDB" only when referring to this project |

---

## Estimated Effort

| Category | Files | Changes |
|---|---|---|
| Heavy (arch, gaps, api-parity, init-impl-plan) | 4 | ~3,000 line changes |
| Medium (dali-vs-marten) | 1 | ~200 line changes |
| Light (remaining plan + spec) | ~14 | ~50-200 line changes each |
| **Total** | **~19 files** | **~4,000 line changes** |

Most changes are mechanical find-replace using the API rename mapping table above.
