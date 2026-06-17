# Backup PostgreSQL database to ./backups/
param(
    [string]$DbHost = "localhost",
    [int]$Port = 5432,
    [string]$User = "postgres",
    [string]$Password = "1",
    [string]$Database = "workloaddb",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$env:PGPASSWORD = $Password

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path (Split-Path -Parent $PSScriptRoot) "backups"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$file = Join-Path $OutputDir "$Database`_$stamp.dump"

Write-Host "Backing up '$Database' to $file ..."
& pg_dump -h $DbHost -p $Port -U $User -Fc $Database -f $file
if ($LASTEXITCODE -ne 0) { throw "pg_dump failed" }

Write-Host "Done."
