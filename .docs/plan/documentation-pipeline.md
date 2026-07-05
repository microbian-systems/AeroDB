# Documentation Pipeline Implementation Plan

## Stack

- **Starlight** (Astro) — guides, tutorials, concepts, examples, FAQ, roadmap
- **DocFX** — generated API reference from XML doc comments
- **GitHub Actions** — automated build and deployment
- **GitHub Pages** — hosting (`docs.aerodb.io`)

---

## 1. Directory Structure

```
AeroDB/
├── docs/                             # Starlight content site
│   ├── astro.config.mjs
│   ├── package.json
│   ├── src/
│   │   ├── content/
│   │   │   ├── index.mdx
│   │   │   ├── getting-started/
│   │   │   │   ├── installation.md
│   │   │   │   ├── quick-start.md
│   │   │   │   ├── first-database.md
│   │   │   │   └── configuration.md
│   │   │   ├── concepts/
│   │   │   │   ├── architecture.md
│   │   │   │   ├── storage-engine.md
│   │   │   │   ├── transactions.md
│   │   │   │   ├── indexes.md
│   │   │   │   └── replication.md
│   │   │   ├── guides/
│   │   │   │   ├── crud.md
│   │   │   │   ├── queries.md
│   │   │   │   ├── linq.md
│   │   │   │   ├── async-api.md
│   │   │   │   ├── backups.md
│   │   │   │   └── encryption.md
│   │   │   ├── examples/
│   │   │   │   ├── console.md
│   │   │   │   ├── aspnet.md
│   │   │   │   ├── blazor.md
│   │   │   │   └── maui.md
│   │   │   ├── advanced/
│   │   │   │   ├── performance.md
│   │   │   │   ├── caching.md
│   │   │   │   ├── custom-serialization.md
│   │   │   │   └── plugins.md
│   │   │   ├── faq.md
│   │   │   ├── roadmap.md
│   │   │   └── api/                # DocFX output mounted here
│   │   └── assets/
│   └── public/
├── docfx/                           # DocFX configuration
│   ├── docfx.json
│   ├── index.md
│   └── toc.yml
├── .github/
│   └── workflows/
│       └── docs.yml                 # Build + deploy workflow
├── src/                             # Library source (XML docs emitted)
├── tests/
├── samples/
└── .docs/                           # Internal planning (this file)
```

---

## 2. Phase 1 — Enable XML Documentation

Add to every `.csproj` in `src/`:

```xml
<PropertyGroup>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <NoWarn>$(NoWarn);1591</NoWarn>  <!-- suppress missing-xml-comment warnings -->
</PropertyGroup>
```

**Step:** Audit existing XML comments. Immediate goal: document all public API surface in `AeroDB` and `AeroDB.AspNetIdentity`. Add `///` comments to every public class, method, property, and parameter.

---

## 3. Phase 2 — DocFX Setup

### 3.1 Initialize

```bash
# Create config
mkdir docfx
```

### 3.2 `docfx/docfx.json`

```json
{
  "metadata": [
    {
      "src": [
        {
          "files": [
            "src/AeroDB/bin/Release/net10.0/AeroDB.dll",
            "src/AeroDB.AspNetIdentity/bin/Release/net10.0/AeroDB.AspNetIdentity.dll"
          ],
          "src": "../"
        }
      ],
      "dest": "api",
      "disableGitFeatures": false
    }
  ],
  "build": {
    "content": [
      {
        "files": [
          "api/**.yml",
          "index.md"
        ]
      }
    ],
    "resource": [
      {
        "files": [
          "images/**"
        ]
      }
    ],
    "dest": "../docs/src/content/api",
    "globalMetadata": {
      "_appTitle": "AeroDB API Reference",
      "_appFooter": "AeroDB Documentation"
    }
  }
}
```

### 3.3 `docfx/toc.yml`

```yaml
- name: API Reference
  href: api/
- name: Namespaces
  items:
    - name: AeroDB
      href: api/AeroDB.yml
    - name: AeroDB.AspNetIdentity
      href: api/AeroDB.AspNetIdentity.yml
```

---

## 4. Phase 3 — Starlight Setup

### 4.1 Initialize

```bash
npm create astro@latest docs -- --template starlight
```

### 4.2 `docs/astro.config.mjs`

```js
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  site: 'https://docs.aerodb.io',
  integrations: [
    starlight({
      title: 'AeroDB Documentation',
      description: 'Fast, multi-model document database for .NET',
      logo: {
        src: './src/assets/aerodb-logo.svg',
      },
      social: {
        github: 'https://github.com/microbians/AeroDB',
      },
      sidebar: [
        {
          label: 'Getting Started',
          items: [
            { label: 'Installation', slug: 'getting-started/installation' },
            { label: 'Quick Start', slug: 'getting-started/quick-start' },
            { label: 'First Database', slug: 'getting-started/first-database' },
            { label: 'Configuration', slug: 'getting-started/configuration' },
          ],
        },
        {
          label: 'Concepts',
          items: [
            { label: 'Architecture', slug: 'concepts/architecture' },
            { label: 'Storage Engine', slug: 'concepts/storage-engine' },
            { label: 'Transactions', slug: 'concepts/transactions' },
            { label: 'Indexes', slug: 'concepts/indexes' },
          ],
        },
        {
          label: 'Guides',
          items: [
            { label: 'CRUD Operations', slug: 'guides/crud' },
            { label: 'Queries', slug: 'guides/queries' },
            { label: 'LINQ', slug: 'guides/linq' },
            { label: 'Async API', slug: 'guides/async-api' },
          ],
        },
        {
          label: 'Examples',
          items: [
            { label: 'Console App', slug: 'examples/console' },
            { label: 'ASP.NET Core', slug: 'examples/aspnet' },
            { label: 'Blazor', slug: 'examples/blazor' },
          ],
        },
        {
          label: 'Advanced',
          items: [
            { label: 'Performance', slug: 'advanced/performance' },
            { label: 'Caching', slug: 'advanced/caching' },
            { label: 'Custom Serialization', slug: 'advanced/custom-serialization' },
          ],
        },
        {
          label: 'API Reference',
          items: [
            { label: 'AeroDB Namespace', slug: 'api/AeroDB' },
            { label: 'AeroDB.AspNetIdentity', slug: 'api/AeroDB.AspNetIdentity' },
          ],
        },
        { label: 'FAQ', slug: 'faq' },
        { label: 'Roadmap', slug: 'roadmap' },
      ],
      customCss: [
        './src/styles/custom.css',
      ],
    }),
  ],
});
```

---

## 5. Phase 4 — GitHub Actions Workflow

### 5.1 `.github/workflows/docs.yml`

```yaml
name: Build & Deploy Docs

on:
  push:
    branches: [main, develop]
  workflow_dispatch:

permissions:
  contents: read
  pages: write
  id-token: write

concurrency:
  group: pages
  cancel-in-progress: true

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore & Build (Release with XML docs)
        run: |
          dotnet restore
          dotnet build -c Release

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: 22
          cache: npm
          cache-dependency-path: docs/package-lock.json

      - name: Install DocFX
        run: dotnet tool restore

      - name: Generate API Docs (DocFX)
        run: dotnet docfx docfx/docfx.json

      - name: Install Starlight dependencies
        working-directory: docs
        run: npm ci

      - name: Build Starlight site
        working-directory: docs
        run: npm run build

      - name: Upload artifact
        uses: actions/upload-pages-artifact@v3
        with:
          path: docs/dist

  deploy:
    if: github.ref == 'refs/heads/main'
    needs: build
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - name: Deploy to GitHub Pages
        id: deployment
        uses: actions/deploy-pages@v4
```

### 5.2 Required: `.config/dotnet-tools.json`

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "docfx": {
      "version": "2.78.2",
      "commands": ["docfx"]
    }
  }
}
```

Run: `dotnet new tool-manifest` then `dotnet tool install docfx`

---

## 6. Writing Conventions

- **One concept per page** — each page should answer a single question
- **Code-first navigation** — every concept page includes a runnable code snippet
- **"Why" before "how"** — explain the rationale, then the implementation
- **Common mistakes** — each guide page ends with a `## Common Mistakes` section
- **AI-friendly** — focused samples, clear intent, cross-linked topics
- **Progressive disclosure** — Getting Started → Concepts → Guides → Advanced → API Ref

---

## 7. Versioning Strategy

### Approach
- ✅ **Stable (latest)** — built from `main` branch
- ✅ **Next (preview)** — optionally built from `develop` branch
- 🔜 **Versioned (v1.x, v2.x)** — added when first major version ships

### Implementation
- Starlight does not have built-in versioning, but supports it via:
  - Community package `@astrojs/starlight-versions`
  - Separate subdirectories: `content/v1/`, `content/v2/` with version picker component
- DocFX: generate API reference per-major-version by tagging builds
- Git tags (`v1.0.0`, `v2.0.0`) trigger versioned doc builds

### When to add versioning
- Launch with single-version (latest/main) only
- Add versioned docs when v2 ships

---

## 8. Implementation Phases

| Phase | Deliverable | Effort |
|-------|------------|--------|
| 1 | Enable XML docs in all `.csproj` files | 1 hr |
| 2 | DocFX config + `docfx.json` | 2 hrs |
| 3 | Starlight scaffold + sidebar config | 2 hrs |
| 4 | GitHub Actions workflow | 2 hrs |
| 5 | GitHub Pages + custom domain (`docs.aerodb.io`) | 1 hr |
| 6 | Write Getting Started content | 4 hrs |
| 7 | Write Concepts + Guides content | 8 hrs |
| 8 | Write Examples content | 4 hrs |
| 9 | Write Advanced + FAQ + Roadmap | 4 hrs |

**Total estimated: ~28 hrs** (spread across weeks)

---

## 9. Custom Domain

1. Configure DNS: `CNAME docs` → `<org>.github.io`
2. Add `public/CNAME` file in Starlight project with `docs.aerodb.io`
3. GitHub Pages: set custom domain in repo Settings → Pages
4. Enforce HTTPS
