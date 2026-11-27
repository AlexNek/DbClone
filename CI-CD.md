# CI/CD — GitHub Actions Workflows

DbClone uses GitHub Actions for automated builds, tests, and installer shipping.
There are three workflows and two shared composite actions.

## Workflows at a glance

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `ci.yml` | push/PR to `main` or `develop`, manual | Quality gate: build and test in parallel jobs (quick suites, layout characterization, and on pushes the installer compile check) |
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
4. Restore the NuGet package cache (`~/.nuget/packages`), keyed on the
   declared packages (`**/*.csproj`, `**/*.props`, `**/*.targets`).
   `actions/setup-dotnet` does not cache packages, so without this every run
   downloaded the whole package graph before the first compile.

> The workflow must run `actions/checkout@v5` with `fetch-depth: 0` **before**
> using this action (or any local composite action) — otherwise the
> `.github/actions/` files aren't on disk yet.

### `.github/actions/build-installer`

Builds the WiX installer and optionally uploads the artifacts. Inputs:

- `version` — product version (`MajorMinorPatch`), **required**.
- `upload` — `'true'` (default) to upload artifacts, `'false'` to only verify
  the build compiles.

Steps:

1. Run `build-installer-wix.ps1 -Version <version>`, which fetches the WebView2
   prerequisite, publishes the app, builds the MSI, and builds the Burn bundle
   (`DbClone-Setup-*.exe`).
2. If `upload` is `'true'`, upload `artifacts/DbClone-Setup-*.exe` and
   `artifacts/DbClone-*.msi` as a workflow artifact (7-day retention).

The prerequisite download is the only network step: it must reach
`go.microsoft.com` (see [WebView2 prerequisite](#webview2-prerequisite)).

## CI workflow (`ci.yml`)

**When it runs:** every push and pull request to `main` or `develop`, plus
manual dispatch (`workflow_dispatch`).

**What it does — three jobs that run at the same time:**

1. `build-and-test` — the check a reviewer waits for. All jobs start from
   `actions/checkout` with `fetch-depth: 0` (required for both local actions
   and GitVersion) and `setup-env` (tooling, version, package cache):
   - `dotnet restore DbClone.slnx`, then `dotnet build DbClone.slnx -c Release`.
   - `dotnet test` for `Application.Tests`, `DbAdapter.PostgreSql.Tests`,
     `DatabaseInterchange.Tests`, and `UI.Tests` filtered to
     `--filter "Speed!=Slow"`. Every test step passes `--no-build`: the build
     step compiled these projects already, and without it each suite spent
     another pass over the graph and re-ran the UI project's post-build
     publish of the PostgreSQL plugin.
2. `characterization` — the measured layout acceptance and the big-catalog
   stress, in two matrix slices (`Speed=Slow` split by test class). These
   searches are single-threaded and minutes long, and their assertions read
   the clock: the characterization table's `ElapsedMilliseconds` bounds and the
   engine's own watchdog behind the `IsDegraded`/`IsRough` flags are only
   meaningful while nothing else competes for the processor. So they get quiet
   runners of their own, and the pull request waits for the longest slice
   instead of for every search in a row. The detailed console logger
   (`--logger "console;verbosity=detailed"`) is deliberate and load-bearing
   here: the measured tables the refactor checklists record are written
   through `ITestOutputHelper`, and MSBuild verbosity alone never puts a
   passing test's output in the log.
3. `installer` — `build-installer` with `upload: 'false'`: verify the installer
   still builds, without uploading artifacts. Skipped on pull requests.

**Why the installer is not built on a pull request:** it is two self-contained
publishes and the WiX chain — minutes that a dialog or a query change has to
wait through, and packaging does not depend on them. It is verified on every
push to `main`/`master`/`develop`, and built for real with the artifacts
attached by `release.yml` on a tag, so a broken package still cannot reach a
release unnoticed. Artifacts are never uploaded from CI: CI is a quality gate,
not a delivery — see Release below.

**Which tests are `Slow`:** the traits mark the tests that spend their time in
the layout search (the 1 000- and 2 000-table stress runs, the catalog-shaped
characterization, the seeded-catalog watchdog checks), not tests that are
merely numerous. A test carries no trait unless it costs seconds on a quiet
machine, so `Speed!=Slow` really is the quick path — measured on the same tree:
the whole `UI.Tests` project, 541 tests, answers in 1 m 2 s, while the 23
`Speed=Slow` layout searches behind it need roughly 9½ minutes.

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

## WebView2 prerequisite

The relationship view (`View relationships` under either connection panel) is
hosted in an in-app window backed by the WebView2 Evergreen runtime. The bundle
installs it as a **non-vital** prerequisite:

1. `build-installer-wix.ps1` downloads Microsoft's bootstrapper
   (`MicrosoftEdgeWebView2Setup.exe`, via
   `https://go.microsoft.com/fwlink/p/?LinkId=2124703`) into
   `artifacts/prerequisites/` and reuses it when the file is already there.
2. `installer/DbClone.Installer.Bundle/Bundle.wxs` binds that folder
   (`!(bindpath.Prerequisites)`) and chains an `ExePackage` **before** the MSI,
   running `/silent /install`.
3. Detection reads the client's `pv` value under
   `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`
   or its `HKCU` counterpart, so an already-installed runtime — per-machine or
   per-user — is not touched.
4. `Permanent="yes"` keeps the runtime when DbClone is uninstalled (other
   software shares it). `Vital="no"` means a prerequisite that cannot install
   does **not** fail the product's install: the app falls back to the system
   browser and states why.

Because the bundle project binds a downloaded file, the WiX build itself never
reaches the network, and `artifacts/` is git-ignored — a fresh clone must run
the script (or the `build-installer` action) rather than build the bundle
project directly.

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
