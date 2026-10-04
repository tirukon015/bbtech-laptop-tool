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

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "The .NET SDK was not found. Install it from https://dot.net (build machine only)." }

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
Write-Host "== Building application (Release)" -ForegroundColor Cyan
& $dotnet build (Join-Path $root 'src\BBTech.LaptopTool') -c Release --no-incremental
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# ---- 4. Release ---------------------------------------------------------------------
$bin = Join-Path $root 'src\BBTech.LaptopTool\bin\Release\net472'
$rel = Join-Path $root 'dist\release'
if (Test-Path $rel) { Remove-Item -LiteralPath $rel -Recurse -Force }
New-Item -ItemType Directory -Force $rel | Out-Null
Copy-Item (Join-Path $bin 'BBTech Laptop Tool.exe') $rel

$exe = Get-Item (Join-Path $rel 'BBTech Laptop Tool.exe')
$hash = (Get-FileHash $exe.FullName -Algorithm SHA256).Hash
Set-Content (Join-Path $root 'dist\release.sha256.txt') "$hash  BBTech Laptop Tool.exe"
Write-Host ("== Done: {0}  ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
Write-Host "   Copy this one file to the USB drive."
