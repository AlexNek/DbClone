# build-installer-wix.ps1 — Build DbClone WiX installer
# Requires: .NET SDK 10+ (WiX builds via WixToolset.Sdk — no WiX CLI needed)
#
# Single source of truth: ONE installer definition produces both artifacts
# per platform - the Burn bundle exe (individual users, custom WPF wizard)
# and the MSI (enterprise deployments, SCCM/Intune/GPO). Both are built in
# this one pipeline from the same sources; the MSI is simultaneously the
# bundle's payload. The only legitimate variant dimension is the target
# platform (Runtime), which flows through every build step.
#
# Usage:
#   .\build-installer-wix.ps1                          # win-x64, GitVersion version
#   .\build-installer-wix.ps1 -Version 2.1.0           # explicit version
#   .\build-installer-wix.ps1 -Runtime win-arm64       # different platform

param(
    [string]$Version = "",
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot

# MSI/bundle InstallerPlatform derived from the runtime (single source of truth)
$MsiPlatform = switch ($Runtime) {
    "win-x64"   { "x64" }
    "win-x86"   { "x86" }
    "win-arm64" { "arm64" }
}

# --- Determine version ---
# GitVersion.Tool is declared in .config/dotnet-tools.json (6.x, matching
# GitVersion.MsBuild), so local builds resolve the same version as CI
# without requiring a global tool install.
if (-not $Version) {
    try {
        Push-Location $Root
        dotnet tool restore | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }
        $gv = dotnet gitversion /output json | ConvertFrom-Json
        if (-not $gv.MajorMinorPatch) { throw "GitVersion returned no version" }
        $Version = $gv.MajorMinorPatch
        Write-Host "GitVersion: $Version" -ForegroundColor Cyan
    }
    catch {
        $Version = "0.0.1"
        Write-Host "GitVersion not available, using fallback: $Version" -ForegroundColor Yellow
    }
    finally {
        Pop-Location
    }
}

Write-Host "`n=== Building DbClone WiX Installer v$Version ($Runtime) ===" -ForegroundColor Green

# The default platform keeps the historical artifact name (no suffix) so
# existing links, docs and the autoupdate fallback keep working; other
# platforms are suffixed, e.g. DbClone-Setup-2.0.0-win-arm64.exe.
$ArtifactName = if ($Runtime -eq "win-x64") { "DbClone-Setup-$Version.exe" } else { "DbClone-Setup-$Version-$Runtime.exe" }
$MsiArtifactName = if ($Runtime -eq "win-x64") { "DbClone-$Version.msi" } else { "DbClone-$Version-$Runtime.msi" }

New-Item -ItemType Directory -Path "$Root\artifacts" -Force | Out-Null

# Report the resolved version back to build-installer.bat for its summary
Set-Content -Path "$Root\artifacts\.last-version" -Value $Version

# --- Step 1: Publish the app ---
Write-Host "`n[1/4] Publishing self-contained ($Runtime)..." -ForegroundColor Cyan
dotnet publish "$Root\src\DbClone.UI\DbClone.UI.csproj" `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishDir=bin/publish `
    -p:Version=$Version

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# --- Step 2: Build MSI ---
Write-Host "`n[2/4] Building MSI package..." -ForegroundColor Cyan
dotnet build "$Root\installer\DbClone.Installer.Msi\DbClone.Installer.Msi.wixproj" `
    -c Release `
    -p:Version=$Version `
    -p:InstallerPlatform=$MsiPlatform

if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

# --- Step 3: Publish Installer UI (self-contained single-file) ---
Write-Host "`n[3/4] Publishing Installer UI..." -ForegroundColor Cyan
dotnet publish "$Root\installer\DbClone.Installer.UI\DbClone.Installer.UI.csproj" `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishDir=bin/publish

if ($LASTEXITCODE -ne 0) { throw "Installer UI publish failed" }

# --- Step 4: Build Bundle (bootstrapper) ---
Write-Host "`n[4/4] Building Burn bundle..." -ForegroundColor Cyan
dotnet build "$Root\installer\DbClone.Installer.Bundle\DbClone.Installer.Bundle.wixproj" `
    -c Release `
    -p:Version=$Version `
    -p:InstallerPlatform=$MsiPlatform

if ($LASTEXITCODE -ne 0) { throw "Bundle build failed" }

# --- Copy outputs to artifacts ---
# Both artifacts come from this single pipeline: the bundle exe (individual
# users) and the MSI (enterprise deployments; also the bundle's payload).
# Copy-Item preserves the source LastWriteTime and incremental builds may not
# touch the outputs, so stamp the artifacts with the current time; they
# always reflect when the build script was last run.
$msiFile = "$Root\installer\DbClone.Installer.Msi\bin\Release\DbClone.msi"
if (Test-Path $msiFile) {
    Copy-Item $msiFile "$Root\artifacts\$MsiArtifactName" -Force
    (Get-Item "$Root\artifacts\$MsiArtifactName").LastWriteTime = Get-Date
    Write-Host "MSI created: artifacts\$MsiArtifactName" -ForegroundColor Green
}
else {
    Write-Host "Warning: MSI not found in expected location. Check build output." -ForegroundColor Yellow
}

$bundleOutput = Get-ChildItem "$Root\installer\DbClone.Installer.Bundle\bin\Release" -Filter "DbClone-Setup-*.exe" -Recurse | Select-Object -First 1

if ($bundleOutput) {
    Copy-Item $bundleOutput.FullName "$Root\artifacts\$ArtifactName" -Force
    (Get-Item "$Root\artifacts\$ArtifactName").LastWriteTime = Get-Date
    Write-Host "Installer created: artifacts\$ArtifactName" -ForegroundColor Green
}
else {
    Write-Host "`nWarning: Bundle .exe not found in expected location. Check build output." -ForegroundColor Yellow
    Write-Host "Looking in: $Root\installer\DbClone.Installer.Bundle\bin\Release\" -ForegroundColor Yellow
}
