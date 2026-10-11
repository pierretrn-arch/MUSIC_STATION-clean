# Enters a real x64 VS 2026 dev environment (cl.exe/link.exe/cmake.exe on PATH with correct
# INCLUDE/LIB), then configures and builds VstNativeShim with CMake+Ninja.
#
# Needed because the Start Menu "Developer PowerShell for VS" shortcuts launch a 32-bit
# (SysWOW64) PowerShell host, which makes Enter-VsDevShell default to x86 tools instead of x64 -
# wrong architecture for this project (matches Surge XT's x86_64-win native module). Passing
# -Arch amd64 explicitly, via Launch-VsDevShell.ps1 directly, sidesteps that default.
param(
    [string]$Config = "RelWithDebInfo",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$vsPath = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath
if (-not $vsPath) { throw "No Visual Studio installation found via vswhere." }

$devShell = Join-Path $vsPath "Common7\Tools\Launch-VsDevShell.ps1"
& $devShell -Arch amd64 -SkipAutomaticLocation

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDir = Join-Path $root "build"

if ($Clean -and (Test-Path $buildDir)) {
    Remove-Item $buildDir -Recurse -Force
}

cmake -S $root -B $buildDir -G Ninja "-DCMAKE_BUILD_TYPE=$Config"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed." }

cmake --build $buildDir
if ($LASTEXITCODE -ne 0) { throw "CMake build failed." }

Write-Host "`nBuild succeeded: $buildDir" -ForegroundColor Green
