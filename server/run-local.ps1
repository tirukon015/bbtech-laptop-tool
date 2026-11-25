<#
.SYNOPSIS
  Runs the BBTech server locally for development or a single-host deployment.
.DESCRIPTION
  1. Starts PostgreSQL (docker-compose.yml) using BBTECH_DB_PASSWORD from server\.env
  2. Applies database migrations
  3. Starts the API on http://localhost:5080 (put a TLS reverse proxy in front for other machines)
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File server\run-local.ps1
  # first time only, in another window:
  dotnet run --project server\BBTech.Api -- create-user touhidul "Touhidul" "Super Admin"
  dotnet run --project server\BBTech.Api -- create-workstation "USB-01"
#>
param([int]$Port = 5080)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$envFile = Join-Path $here '.env'
if (-not (Test-Path $envFile)) { throw "Create server\.env from server\.env.example and set BBTECH_DB_PASSWORD." }
$pw = (Get-Content $envFile | Where-Object { $_ -like 'BBTECH_DB_PASSWORD=*' }) -replace '^BBTECH_DB_PASSWORD=', ''
if (-not $pw) { throw "BBTECH_DB_PASSWORD is empty in server\.env." }

Push-Location $here
try { docker compose up -d | Out-Host } finally { Pop-Location }

$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=5433;Database=bbtech;Username=bbtech;Password=$pw"
$env:ASPNETCORE_URLS = "http://localhost:$Port"
dotnet run --project (Join-Path $here 'BBTech.Api') -- migrate
dotnet run --project (Join-Path $here 'BBTech.Api')
