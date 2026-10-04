<#
.SYNOPSIS
  Copies the reusable tools from TEST-Laptop.zip into third_party\ (allow-list only).

.DESCRIPTION
  The ZIP is opened read-only and never modified. Only these items are imported:
    - BatteryInfoView.exe + BatteryInfoView.cfg   (NirSoft, battery health)
    - HD Sentinel program files                   (drive health; see exclusions)
    - freepcaudit.exe                             (optional full system audit viewer)
    - Speaker/headphone left-right test video     (renamed)
  Deliberately NOT imported:
    - Activation.bat           (downloads and runs a Windows activation/piracy script)
    - HD Sentinel\HDSData\     (disk serial numbers and history of previous customers' laptops)
    - harddisksentinelupdate.exe, HDSentinelTray.exe, hdsaction.exe, removehds7.vbs
                               (internet updater, tray/scheduler helpers - not needed)
    - non-English HD Sentinel help files (size)
    - KeyboardTest.exe, LCDtest.exe, "My Cam.exe" (unsigned legacy utilities of unclear origin;
                               replaced by built-in keyboard, display and camera tests)
    - desktop.ini, shortcuts, "._" macOS metadata, "- Copy" duplicates

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\import-toolkit.ps1 -Zip "C:\Users\me\Desktop\TEST-Laptop.zip"
#>
param([Parameter(Mandatory = $true)][string]$Zip)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dest = Join-Path $root 'third_party'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("bbtech-import-" + [Guid]::NewGuid().ToString('N'))

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $Zip), $tmp)
$src = Join-Path $tmp 'TEST-Laptop'
if (-not (Test-Path $src)) { $src = $tmp }

if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
$biv = New-Item -ItemType Directory -Force (Join-Path $dest 'tools\BatteryInfoView')
$hds = New-Item -ItemType Directory -Force (Join-Path $dest 'tools\HDSentinel')
$fpa = New-Item -ItemType Directory -Force (Join-Path $dest 'tools\FreePCAudit')
$tst = New-Item -ItemType Directory -Force (Join-Path $dest 'tests')

Copy-Item (Join-Path $src 'BatteryInfoView.exe') $biv
Copy-Item (Join-Path $src 'BatteryInfoView.cfg') $biv
Copy-Item (Join-Path $src 'freepcaudit.exe') $fpa
Copy-Item (Join-Path $src 'Speaker Headphones Left And Right Test(1080P_HD) - Copy.mp4') (Join-Path $tst 'Speaker Headphones Left And Right Test.mp4')

$hdsSrc = Join-Path $src 'HD Sentinel'
$exclude = @('harddisksentinelupdate.exe', 'HDSentinelTray.exe', 'hdsaction.exe', 'removehds7.vbs')
Get-ChildItem $hdsSrc -File | Where-Object {
    $exclude -notcontains $_.Name -and -not ($_.Extension -eq '.chm' -and $_.Name -ne 'en.chm')
} | Copy-Item -Destination $hds

# Fresh, sanitised data folder: settings only, no previous-customer disk data.
$data = New-Item -ItemType Directory -Force (Join-Path $hds 'HDSData')
$ini = Get-Content (Join-Path $hdsSrc 'HDSData\HDSentinel.ini')
$clean = foreach ($line in $ini) {
    if ($line -match '^StatusWin=') { 'StatusWin=' ; continue }                 # contained disk serials
    if ($line -match '^AlertFile=') { 'AlertFile=' ; continue }                 # absolute path from old PC
    if ($line -match '^Load_' -and $line -notmatch '^Load_(Admin|Administrator|User|bb|yoga|X1)=') { continue } # user names
    if ($line -match '^Load_') { ($line -replace '=.*$', '=No'); continue }      # never auto-start with Windows
    if ($line -match '^Splash=') { 'Splash=No'; continue }
    if ($line -match '^Periodic=') { 'Periodic=No'; continue }                  # no update checks
    $line
}
$clean = $clean | Where-Object { $_ -notmatch '^Load_(bb|yoga|X1)=' }
Set-Content -Path (Join-Path $data 'HDSentinel.ini') -Value $clean -Encoding Default

Remove-Item $tmp -Recurse -Force
Write-Host "Imported toolkit into $dest"
Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum | ForEach-Object { "{0} files, {1:N1} MB" -f $_.Count, ($_.Sum / 1MB) }
