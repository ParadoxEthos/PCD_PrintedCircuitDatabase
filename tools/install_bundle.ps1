# Install (or dev-link) the PCD bundle into AutoCAD's per-user trusted plugin location.
#
#   powershell -ExecutionPolicy Bypass -File tools\install_bundle.ps1          # copy
#   powershell -ExecutionPolicy Bypass -File tools\install_bundle.ps1 -Dev     # junction (dev)
#   powershell -ExecutionPolicy Bypass -File tools\install_bundle.ps1 -Uninstall
#
# Target: %APPDATA%\Autodesk\ApplicationPlugins\PCD.bundle
#   AutoCAD scans ApplicationPlugins at startup, trusts what it finds there (no SECURELOAD
#   prompt), and demand-loads per PackageContents.xml. Per-user, so no admin rights needed.
#
# -Dev makes a directory junction to the repo's built bundle, so a rebuild is picked up on the
#   next AutoCAD start without re-copying. A junction needs no elevation (unlike a symlink).
# Default (copy) is what a real install does: a self-contained copy independent of the repo.
#
# Run tools\build_bundle.ps1 first so Contents\ holds the DLLs.
param([switch]$Dev, [switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$root   = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'bundle\PCD.bundle'
$target = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\PCD.bundle'

function Remove-Target {
    if (-not (Test-Path $target)) { return }
    # A junction must be removed as a reparse point, not recursed into (that would delete the repo).
    $item = Get-Item $target -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        # .NET Directory.Delete on a junction removes the link only.
        [System.IO.Directory]::Delete($target, $false)
    } else {
        Remove-Item $target -Recurse -Force
    }
}

if ($Uninstall) {
    Remove-Target
    Write-Host "Uninstalled: $target"
    return
}

if (-not (Test-Path (Join-Path $source 'PackageContents.xml'))) { throw "No manifest at $source" }
$dll = Join-Path $source 'Contents\PCD.Loader.dll'
if (-not (Test-Path $dll)) { throw "Contents\PCD.Loader.dll missing -- run tools\build_bundle.ps1 first" }

$parent = Split-Path $target -Parent
if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
Remove-Target

if ($Dev) {
    New-Item -ItemType Junction -Path $target -Target $source | Out-Null
    Write-Host "Dev junction: $target  ->  $source"
} else {
    Copy-Item $source $target -Recurse -Force
    Write-Host "Installed (copy): $target"
}
Write-Host "Restart AutoCAD, then type PCD. (Bundle is demand-loaded on first PCD command.)"
