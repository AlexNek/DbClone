@echo off
setlocal

title DbClone Installer Builder (WiX)

echo ========================================
echo   DbClone Installer Builder (WiX)
echo ========================================
echo.
echo Usage:
echo   build-installer.bat                   full build, win-x64 (default platform)
echo   build-installer.bat 2.1.0             explicit version
echo   build-installer.bat 2.1.0 win-arm64   explicit version and platform
echo.

:: ------------------------------------------------------------
:: Parse arguments: optional version, optional runtime/platform
:: ------------------------------------------------------------

set "VERSION=%~1"
set "RUNTIME=%~2"

:: ------------------------------------------------------------
:: Check prerequisites (WiX builds via WixToolset.Sdk, so only
:: the .NET SDK is required - no WiX CLI install needed)
:: ------------------------------------------------------------

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK is not installed.
    echo.
    echo Install .NET SDK 10+:
    echo https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

:: ------------------------------------------------------------
:: Version: explicit argument, or resolved inside the PowerShell
:: script via GitVersion (local tool manifest, .config/dotnet-tools.json).
:: The script reports the resolved version back through
:: artifacts\.last-version so the summary below shows real names.
:: ------------------------------------------------------------

if defined VERSION echo Version: %VERSION%
if defined RUNTIME echo Platform: %RUNTIME%
echo.

:: ------------------------------------------------------------
:: Build via PowerShell WiX script (single installer: bundle exe)
:: ------------------------------------------------------------

if defined RUNTIME (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer-wix.ps1" -Version "%VERSION%" -Runtime "%RUNTIME%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer-wix.ps1" -Version "%VERSION%"
)

if errorlevel 1 (
    echo.
    echo ERROR: WiX build failed.
    echo.
    pause
    exit /b 1
)

:: Resolve the version the PowerShell script actually used (auto-detection)
if not defined VERSION if exist "%~dp0artifacts\.last-version" set /p VERSION=<"%~dp0artifacts\.last-version"
if not defined VERSION set "VERSION=0.0.1"

echo.
echo ========================================
echo Successfully created installers:
echo.
if defined RUNTIME (
    if /I not "%RUNTIME%"=="win-x64" (
        echo   artifacts\DbClone-Setup-%VERSION%-%RUNTIME%.exe
        echo   artifacts\DbClone-%VERSION%-%RUNTIME%.msi
    ) else (
        echo   artifacts\DbClone-Setup-%VERSION%.exe
        echo   artifacts\DbClone-%VERSION%.msi
    )
) else (
    echo   artifacts\DbClone-Setup-%VERSION%.exe
    echo   artifacts\DbClone-%VERSION%.msi
)
echo ========================================
echo.

pause
endlocal
