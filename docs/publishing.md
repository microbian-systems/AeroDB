# Publishing

Dali uses Trusted Publishing (OIDC) to push packages to NuGet.org — no long-lived API keys in CI. The release pipeline is fully automated via GitHub Actions.

## Pipeline Overview

| Workflow | File | Trigger | NuGet version | GitHub Release |
|---|---|---|---|---|
| **Develop Preview** | `publish-develop.yml` | `push` → `develop` | `0.0.6-alpha.{n}` | ❌ |
| **Release** | `publish-release.yml` | `push` tag `v*` | `1.2.0` (from tag) | ✅ auto notes |
| **Manual Preview** | `publish-preview.yml` | `workflow_dispatch` | custom suffix | ❌ |

## Prerequisites

### 1. Trusted Publishing policies on NuGet.org

Go to **nuget.org** → your profile → **Trusted Publishing** → **Add Publisher** for each workflow file:

| Field | Value |
|---|---|
| Repository Owner | `microbian-systems` |
| Repository | `Dali` |
| Policy Owner | `microbian-systems` (org) |

Each policy only differs by **Workflow File**:

| Policy | Workflow File |
|---|---|
| Release | `publish-release.yml` |
| Develop Preview | `publish-develop.yml` |
| Manual Preview | `publish-preview.yml` |

### 2. GitHub Actions variable

Set `NUGET_USER` in the repo:

**Settings** → **Secrets and variables** → **Actions** → **Variables** tab → **New variable**

`NUGET_USER` = your nuget.org **profile name** (not email, not username — the name shown on your nuget.org profile).

## Release Flow (Production)

```bash
git tag v1.2.0
git push origin v1.2.0
```

This triggers `publish-release.yml`, which:

1. Checks out the repo (with submodules)
2. Extracts `1.2.0` from the tag name
3. Packs all 5 library projects as stable packages (`1.2.0`, no suffix)
4. Requests a short-lived NuGet API key via OIDC
5. Pushes `.nupkg` and `.snupkg` to NuGet.org
6. Creates a GitHub Release with auto-generated release notes

### Before releasing

Update `src/Directory.Build.props` to the next version:

```xml
<VersionPrefix>0.1.0</VersionPrefix>         <!-- target release version -->
<VersionSuffix>alpha</VersionSuffix>          <!-- follows after release -->
```

The `VersionPrefix` in the props file serves as the base for preview builds.
The tag value overrides both during a release (via `-VersionPrefix`).

## Preview Flows

### Auto: push to develop

Every push to the `develop` branch triggers `publish-develop.yml`, which packs with a unique suffix and pushes to NuGet:

```
0.0.6-alpha.42
0.0.6-alpha.43
0.0.6-alpha.44
```

Each run uses `github.run_number` for a strictly increasing, conflict-free version.

### Manual: custom suffix

Run **Publish to NuGet (Preview)** from GitHub Actions → **Run workflow** and enter a suffix:

| Suffix | Resulting version |
|---|---|
| `alpha` (default) | `0.0.6-alpha` |
| `rc.1` | `0.0.6-rc.1` |
| `beta` | `0.0.6-beta` |
| `preview.20250624` | `0.0.6-preview.20250624` |

## Local Publishing

The `push.ps1` script at the repo root packs and publishes locally using `$env:NUGET_API_KEY`:

```powershell
./push.ps1                        # preview (0.0.6-alpha)
./push.ps1 -Stable                # stable   (0.0.6)
./push.ps1 -VersionSuffix "rc.1"  # custom   (0.0.6-rc.1)
```

This is for testing — CI is the primary publishing path.

## Script Reference

### `build/nuget-pack.ps1`

Packs all 5 library projects in Release mode with symbol packages.

```powershell
./build/nuget-pack.ps1                          # 0.0.6-alpha
./build/nuget-pack.ps1 -Stable                   # 0.0.6
./build/nuget-pack.ps1 -Stable -VersionPrefix 1.2.0   # 1.2.0
./build/nuget-pack.ps1 -VersionSuffix "rc.1"    # 0.0.6-rc.1
```

Output: `build/nupkgs/`

| Flag | Effect |
|---|---|
| `-VersionPrefix` | Overrides `VersionPrefix` from `Directory.Build.props` (used by release workflow) |
| `-VersionSuffix` | Appends a SemVer 2.0 suffix; defaults to `alpha` |
| `-Stable` | Strips the suffix entirely; overrides `-VersionSuffix` |
| `-OutputDir` | Output directory (default: `build/nupkgs/`) |
| `-Configuration` | Build configuration (default: `Release`) |

**Projects packed:**

- `src/Dali`
- `src/Dali.EntityFrameworkCore`
- `src/Dali.ML`
- `src/Dali.SourceGenerators`
- `src/Dali.WolverineFx`

### `build/nuget-publish.ps1`

Pushes `.nupkg` and `.snupkg` files to NuGet.org.

```powershell
./build/nuget-publish.ps1                              # uses $env:NUGET_API_KEY
./build/nuget-publish.ps1 -ApiKey "<temp-key>"          # explicit (CI)
./build/nuget-publish.ps1 -ApiKey "<key>" -SkipSnupkg   # no symbol packages
```

## Version Lifecycle Example

```
develop branch push → 0.1.0-alpha.42    (auto preview)
develop branch push → 0.1.0-alpha.43    (auto preview)
...                                     (more previews)
git tag v0.1.0 && push → 0.1.0          (auto stable + GitHub Release)
                                             ↓ manual bump in Directory.Build.props
develop branch push → 0.1.1-alpha.44    (auto preview)
develop branch push → 0.1.1-alpha.45    (auto preview)
git tag v0.1.1 && push → 0.1.1          (auto stable + GitHub Release)
```

## NuGet.org visibility

- Packages with a suffix (e.g. `0.0.6-alpha.42`) are marked as **prerelease** on NuGet.org — users need `dotnet add package Dali --prerelease` to see them
- Packages without a suffix (e.g. `0.1.0`) are shown as **latest stable** by default
