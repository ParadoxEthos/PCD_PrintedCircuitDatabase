# Assemble the shippable AutoCAD plugin bundle.
#
#   powershell -ExecutionPolicy Bypass -File tools\build_bundle.ps1
#
# Builds PCD.Core and PCD.Loader in Release and places both DLLs in
# bundle\PCD.bundle\Contents\, next to the committed PackageContents.xml. The result --
# bundle\PCD.bundle\ -- is a complete Autodesk ApplicationPlugin: copy it into
# %APPDATA%\Autodesk\ApplicationPlugins\ (tools\install_bundle.ps1 does this) and AutoCAD
# auto-trusts and demand-loads it. No NETLOAD, no SECURELOAD prompt.
#
# The AutoCAD reference assemblies are marked Private=false, so only PCD.Core.dll and
# PCD.Loader.dll land in the output -- the bundle never ships Autodesk's own DLLs.
param([switch]$KeepPdb)

$ErrorActionPreference = 'Stop'
$root     = Split-Path $PSScriptRoot -Parent
$contents = Join-Path $root 'bundle\PCD.bundle\Contents'
$core     = Join-Path $root 'core\PCD.Core.csproj'
$loader   = Join-Path $root 'loader\PCD.Loader.csproj'

if (-not (Test-Path $contents)) { New-Item -ItemType Directory -Path $contents -Force | Out-Null }
# Clear stale binaries so a removed file cannot linger in the bundle.
Get-ChildItem $contents -Filter *.dll -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $contents -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "Building PCD.Core   -> $contents"
dotnet build $core   -c Release --no-incremental -o $contents | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "PCD.Core build failed" }

Write-Host "Building PCD.Loader -> $contents"
dotnet build $loader -c Release --no-incremental -o $contents | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "PCD.Loader build failed" }

if (-not $KeepPdb) {
    Get-ChildItem $contents -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
}
# AutoCAD loads plugin assemblies into its own already-initialised runtime and never reads a
# .deps.json; PCD.Core is hot-loaded by path, not as a project reference, so these only describe
# the framework. Drop them so the bundle ships exactly two files plus the manifest.
Get-ChildItem $contents -Filter *.deps.json -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host ""
Write-Host "Bundle contents:"
Get-ChildItem $contents | ForEach-Object { "  {0,-22} {1,8:N0} bytes" -f $_.Name, $_.Length }
$manifest = Join-Path $root 'bundle\PCD.bundle\PackageContents.xml'
if (Test-Path $manifest) { Write-Host "  PackageContents.xml present" }
else { Write-Warning "PackageContents.xml MISSING at $manifest" }
Write-Host ""
Write-Host "Bundle ready: $(Join-Path $root 'bundle\PCD.bundle')"
Write-Host "Install with: tools\install_bundle.ps1"
