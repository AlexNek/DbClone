# CI/CD — GitHub Actions Workflows

DbClone uses GitHub Actions for automated builds, tests, and installer shipping.
There are three workflows and two shared composite actions.

## Workflows at a glance

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `ci.yml` | push/PR to `main` or `develop`, manual | Quality gate: build, test, verify the installer still compiles |
| `release.yml` | `v*` git tag, manual | Ship: build the final installer and publish a GitHub Release |
| `docs.yml` | push to `main` touching `docs/**` or `mkdocs.yml` | Build and deploy the MkDocs user manual to GitHub Pages |

## Shared composite actions

Both `ci.yml` and `release.yml` are built from two reusable steps. This keeps
the workflows short and avoids duplicating logic.

### `.github/actions/setup-env`

Prepares the build environment. Outputs:

- `version` — the resolved `MajorMinorPatch` version from GitVersion.

Steps:

1. Install GitVersion (tool spec `6.x`).
2. Run GitVersion to compute the version from git history and branch config
   (`GitVersion.yml`).
3. Install the .NET SDK (`10.0.x`).

> The workflow must run `actions/checkout@v5` with `fetch-depth: 0` **before**
> using this action (or any local composite action) — otherwise the
> `.github/actions/` files aren't on disk yet.

### `.github/actions/build-installer`

Builds the WiX installer and optionally uploads the artifacts. Inputs:

- `version` — product version (`MajorMinorPatch`), **required**.
- `upload` — `'true'` (default) to upload artifacts, `'false'` to only verify
  the build compiles.

Steps:

1. Run `build-installer-wix.ps1 -Version <version>`, which publishes the app,
   builds the MSI, and builds the Burn bundle (`DbClone-Setup-*.exe`).
2. If `upload` is `'true'`, upload `artifacts/DbClone-Setup-*.exe` and
   `artifacts/DbClone-*.msi` as a workflow artifact (7-day retention).

## CI workflow (`ci.yml`)

**When it runs:** every push and pull request to `main` or `develop`, plus
manual dispatch (`workflow_dispatch`).

**What it does:**

1. `actions/checkout` with `fetch-depth: 0` — full history (required for both
   local actions and GitVersion).
2. `setup-env` — tooling + version.
3. `dotnet restore` — restore NuGet packages.
4. `dotnet build` — compile the whole solution in Release.
5. `dotnet test` — run the `Application.Tests` and `PostgreSql.Tests` suites.
6. `build-installer` with `upload: 'false'` — verify the installer still
   builds, but do **not** upload artifacts.

**Why the installer is built here:** it catches packaging breakage on the pull
request, before a broken package ever reaches a release tag. The artifacts are
not uploaded because CI is a quality gate, not a delivery — see Release below.

## Release workflow (`release.yml`)

**When it runs:** when a `v*` tag (e.g. `v2.1.0`) is pushed, plus manual
dispatch. Requires `contents: write` so it can create the Release.

**What it does:**

1. `actions/checkout` with `fetch-depth: 0` — full history.
2. `setup-env` — tooling + version (matches the tag).
3. `build-installer` with default `upload: 'true'` — build and upload the
   artifacts.
4. `softprops/action-gh-release` — create a GitHub Release with the installer
   attached and auto-generated release notes.

**Why a separate workflow:** a Release is a deliberate, versioned shipping
event. It runs only when you tag a version, and its output is public and
permanent. CI runs constantly but produces nothing users see.

## Docs workflow (`docs.yml`)

**When it runs:** push to `main` changing `docs/**` or `mkdocs.yml`, plus
manual dispatch. Runs on `ubuntu-latest`, builds the MkDocs site with
`mkdocs build --strict` and deploys it to GitHub Pages.

## How to build the installer locally

```powershell
# Uses GitVersion from .config/dotnet-tools.json for the version
.\build-installer-wix.ps1

# Or pin a specific version
.\build-installer-wix.ps1 -Version 2.1.0

# Or a different platform
.\build-installer-wix.ps1 -Runtime win-arm64
```

The script publishes the app, builds the MSI, builds the Burn bundle, and
copies the outputs into `artifacts/`.

## Versioning

Versions are computed by GitVersion (config in `GitVersion.yml`,
`ContinuousDeployment` mode):

| Branch | Tag |
|--------|-----|
| `main` | *(stable)* |
| `develop` | `alpha` |
| `feature/*` | `beta` |
| `release/*` | `rc` |

Bump the version by adding to a commit message: `+semver: major`,
`+semver: minor`, or `+semver: patch`.

## Troubleshooting

- **`WIX0006` / `WIX0010` (empty `Package/@Version`):** the installer projects
  are not in `DbClone.slnx` because they require the publish output produced by
  `build-installer-wix.ps1`. Never build the WiX projects with a bare
  `dotnet build`; always go through the script (directly or via the
  `build-installer` action).
- **GitVersion version mismatch:** the tool spec in `setup-env`, the local tool
  in `.config/dotnet-tools.json`, and the `GitVersion.MsBuild` package in
  `Directory.Build.props` must stay on the same major version (`6.x`).
