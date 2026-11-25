<#
.SYNOPSIS
  Runs the unit tests and builds the single portable executable:
      dist\release\BBTech Laptop Tool.exe

.DESCRIPTION
  1. Packs the trimmed third-party tools from third_party\ into artifacts\bundle\*.zip
  2. Runs the unit tests
  3. Builds the WPF app (Release); the bundle is embedded into the EXE
  4. Copies ONLY the EXE to dist\release\ (no .config, no DLLs, no folders needed)

  Build machine requirements: Windows + .NET SDK 8 or newer.
  Target laptops need nothing (Windows 10/11 x64 include .NET Framework 4.7.2+).

.PARAMETER NoMedia
  Leave out the BBTech speaker test media (third_party\tests\Speaker Headphones Left And Right
  Test.mp4, 18 MB). By default it is embedded so the Speaker test can play it inside the app
  with no extra files. Without it the Speaker test shows "BBTech test media is unavailable."
  and the generated left/right tones still work.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\build.ps1
#>
param([switch]$SkipTests, [switch]$NoMedia)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (Test-Path $localDotnet) {
    $hasSdk = $false
    if ($dotnet -and (Test-Path $dotnet)) {
        try { & $dotnet --version 2>&1 | Out-Null; if ($LASTEXITCODE -eq 0) { $hasSdk = $true } } catch { }
    }
    if (-not $hasSdk) {
        $dotnet = $localDotnet
        $env:DOTNET_ROOT = Split-Path $localDotnet
        $env:PATH = (Split-Path $localDotnet) + ';' + $env:PATH
    }
}
if (-not $dotnet -or -not (Test-Path $dotnet)) { throw "The .NET SDK was not found. Install it from https://dot.net (build machine only)." }

$tp = Join-Path $root 'third_party'
if (-not (Test-Path (Join-Path $tp 'tools'))) { throw "third_party\ is missing. Run: build\import-toolkit.ps1 -Zip <path to TEST-Laptop.zip>" }

# ---- 1. Bundle (trimmed) ------------------------------------------------------------
Write-Host "== Packing embedded tool bundle" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$bundle = Join-Path $root 'artifacts\bundle'
$stage = Join-Path $root 'artifacts\stage'
foreach ($d in $bundle, $stage) { if (Test-Path $d) { Remove-Item -LiteralPath $d -Recurse -Force }; New-Item -ItemType Directory -Force $d | Out-Null }

function Pack($name, $srcDir, [scriptblock]$keep) {
    $s = Join-Path $stage $name
    New-Item -ItemType Directory -Force $s | Out-Null
    Get-ChildItem $srcDir -Recurse -File | Where-Object { & $keep $_ } | ForEach-Object {
        $rel = $_.FullName.Substring($srcDir.Length + 1)
        $dst = Join-Path $s $rel
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Copy-Item $_.FullName $dst
    }
    [IO.Compression.ZipFile]::CreateFromDirectory($s, (Join-Path $bundle "$name.zip"), 'Optimal', $false)
}

Pack 'BatteryInfoView' (Join-Path $tp 'tools\BatteryInfoView') { $true }
Pack 'FreePCAudit' (Join-Path $tp 'tools\FreePCAudit') { $true }
# HD Sentinel: drop help files, non-English UI languages and alert sounds (not used by the tool).
Pack 'HDSentinel' (Join-Path $tp 'tools\HDSentinel') {
    param($f)
    -not ($f.Extension -eq '.chm' -or
          ($f.Extension -eq '.lng' -and @('EN.LNG', 'enpro.lng') -notcontains $f.Name) -or
          @('hds_eula_hu.txt', 'HDsirena.mp3', 'HDnotify.wav', 'hdd.jpg') -contains $f.Name)
}
$media = Join-Path $tp 'tests\Speaker Headphones Left And Right Test.mp4'
if (-not $NoMedia) {
    if (Test-Path $media) { Copy-Item $media (Join-Path $bundle 'SpeakerTest.mp4') }
    else { Write-Warning "Speaker test media not found ($media); building without it." }
}
Remove-Item -LiteralPath $stage -Recurse -Force
Get-ChildItem $bundle | ForEach-Object { "   {0,-22} {1,7:N1} MB" -f $_.Name, ($_.Length / 1MB) }

# ---- 2. Tests -----------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host "== Running unit tests" -ForegroundColor Cyan
    & $dotnet test (Join-Path $root 'tests\BBTech.Core.Tests') -c Release --logger "trx;LogFileName=test-results.trx" --results-directory (Join-Path $root 'dist\test-results')
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }
}

# ---- 3. Build -----------------------------------------------------------------------
# The EXE carries the exact commit; "-dirty" marks a build made from uncommitted changes, so a
# stale or unofficial EXE is recognisable from Properties > Details and from the log line at startup.
$commit = (& git -C $root rev-parse --short=12 HEAD 2>$null)
if (-not $commit) { $commit = 'nogit' }
$dirty = (& git -C $root status --porcelain 2>$null)
if ($dirty) { $commit = "$commit-dirty"; Write-Warning "Building from uncommitted changes: the EXE is stamped '$commit'." }

Write-Host "== Building application (Release, $commit)" -ForegroundColor Cyan
& $dotnet build (Join-Path $root 'src\BBTech.LaptopTool') -c Release --no-incremental "-p:SourceRevisionId=$commit"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# ---- 4. Release ---------------------------------------------------------------------
# ONE release location: dist\release\BBTech Laptop Tool.exe. Any failure stops the build, so an old
# EXE is never left behind next to a new checksum.
$bin = Join-Path $root 'src\BBTech.LaptopTool\bin\Release\net472'
$rel = Join-Path $root 'dist\release'
$targetExe = Join-Path $rel 'BBTech Laptop Tool.exe'
$builtExe = Join-Path $bin 'BBTech Laptop Tool.exe'

if (-not (Test-Path $rel)) { New-Item -ItemType Directory -Force $rel | Out-Null }
try {
    if (Test-Path $targetExe) { Remove-Item -LiteralPath $targetExe -Force -ErrorAction Stop }
    Copy-Item $builtExe $targetExe -Force -ErrorAction Stop
} catch {
    throw "Could not replace '$targetExe' (is BBTech Laptop Tool running from it?). Close it and build again. Nothing was released."
}

# Old duplicate copies are removed so nobody launches them by mistake.
foreach ($stale in @((Join-Path $root 'dist\BBTech Laptop Tool.exe'), (Join-Path $root 'dist\BBTech Laptop Tool.exe.config'), (Join-Path $root 'dist\release.sha256.txt'), (Join-Path $bin 'BBTech Laptop Tool.old.exe'))) {
    if (Test-Path $stale) {
        try { Remove-Item -LiteralPath $stale -Force -ErrorAction Stop; Write-Host "   Removed stale $stale" -ForegroundColor Gray }
        catch { Write-Warning "Could not remove stale copy '$stale'. Delete it by hand." }
    }
}

$hash = (Get-FileHash $targetExe -Algorithm SHA256).Hash
if ($hash -ne (Get-FileHash $builtExe -Algorithm SHA256).Hash) { throw "The released EXE does not match the build output." }
$exe = Get-Item $targetExe
$ver = $exe.VersionInfo.ProductVersion
$stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
Set-Content (Join-Path $rel 'BBTech Laptop Tool.exe.sha256.txt') "$hash  BBTech Laptop Tool.exe`r`nversion: $ver`r`nbuilt: $stamp"
Write-Host ("== Done: {0}  ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
Write-Host "   Version: $ver" -ForegroundColor Green
Write-Host "   SHA-256: $hash" -ForegroundColor Green
Write-Host "   Copy this one file to the USB drive."
