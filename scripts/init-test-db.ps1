# Creates an isolated test database by cloning workloaddb (or initializing from schema).
param(
    [string]$DbHost = "localhost",
    [int]$Port = 5432,
    [string]$User = "postgres",
    [string]$Password = "1",
    [string]$SourceDb = "workloaddb",
    [string]$TestDb = "workloaddb_test"
)

$ErrorActionPreference = "Stop"
$env:PGPASSWORD = $Password

$psqlCmd = Get-Command psql -ErrorAction SilentlyContinue
$psql = if ($psqlCmd) { $psqlCmd.Source } elseif (Test-Path "C:\Program Files\PostgreSQL\16\bin\psql.exe") { "C:\Program Files\PostgreSQL\16\bin\psql.exe" } else { $null }

if (-not $psql) { throw "psql not found. Install PostgreSQL client tools or add psql to PATH." }

function Invoke-Psql([string]$Sql) {
    & $psql -h $DbHost -p $Port -U $User -d postgres -v ON_ERROR_STOP=1 -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $Sql" }
}

Write-Host "Checking PostgreSQL..."
Invoke-Psql "SELECT 1" | Out-Null

$exists = & $psql -h $DbHost -p $Port -U $User -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$TestDb'"
if ($exists -eq "1") {
    Write-Host "Dropping existing database '$TestDb'..."
    Invoke-Psql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$TestDb' AND pid <> pg_backend_pid();"
    Invoke-Psql "DROP DATABASE IF EXISTS $TestDb"
}

$sourceExists = & $psql -h $DbHost -p $Port -U $User -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$SourceDb'"
if ($sourceExists -eq "1") {
    Write-Host "Cloning '$SourceDb' -> '$TestDb'..."
    Invoke-Psql "CREATE DATABASE $TestDb WITH TEMPLATE $SourceDb OWNER $User"
} else {
    Write-Host "Source DB '$SourceDb' not found. Creating empty '$TestDb' and applying schema..."
    Invoke-Psql "CREATE DATABASE $TestDb OWNER $User"
    $root = Split-Path -Parent $PSScriptRoot
    & $psql -h $DbHost -p $Port -U $User -d $TestDb -v ON_ERROR_STOP=1 -f (Join-Path $root "schema.sql")
    if (Test-Path (Join-Path $root "add_auth_tables.sql")) {
        & $psql -h $DbHost -p $Port -U $User -d $TestDb -v ON_ERROR_STOP=1 -f (Join-Path $root "add_auth_tables.sql")
    }
}

Write-Host "Applying seed scripts..."
$seed = Join-Path $PSScriptRoot "seed-dep-managers.sql"
if (Test-Path $seed) {
    & $psql -h $DbHost -p $Port -U $User -d $TestDb -v ON_ERROR_STOP=1 -f $seed
}

Write-Host "Test database '$TestDb' is ready."
