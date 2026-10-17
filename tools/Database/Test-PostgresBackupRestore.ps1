[CmdletBinding()]
param(
    [string]$PostgresImage = "postgres:17-alpine"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$tempDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } elseif ($env:TEMP) { $env:TEMP } else { [System.IO.Path]::GetTempPath() }
$containerName = "vendmachines-backup-source-test-$([Guid]::NewGuid().ToString('N'))"
$backupPath = Join-Path $tempDirectory "vendmachines-backup-test-$([Guid]::NewGuid().ToString('N')).dump"
$containerStarted = $false

function Invoke-DockerChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | Select-Object -Last 3 | Out-String).Trim()
        throw "Falha ao preparar o fixture sintético do backup (código $LASTEXITCODE): $detail"
    }
    return $output
}

try {
    Invoke-DockerChecked @(
        "run", "--rm", "--detach", "--network", "none", "--name", $containerName,
        "--env", "POSTGRES_PASSWORD=synthetic-backup-test-only", $PostgresImage
    ) | Out-Null
    $containerStarted = $true

    $ready = $false
    $consecutiveReadyChecks = 0
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        & docker exec $containerName pg_isready -U postgres *> $null
        if ($LASTEXITCODE -eq 0) {
            $consecutiveReadyChecks++
            if ($consecutiveReadyChecks -ge 3) {
                $ready = $true
                break
            }
        } else {
            $consecutiveReadyChecks = 0
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
        throw "O PostgreSQL sintético não ficou pronto dentro de 60 segundos."
    }

    Invoke-DockerChecked @("exec", $containerName, "createdb", "-U", "postgres", "backup_source") | Out-Null
    $fixtureSql = 'CREATE TABLE "__EFMigrationsHistory" ("MigrationId" character varying(150) PRIMARY KEY, "ProductVersion" character varying(32) NOT NULL); INSERT INTO "__EFMigrationsHistory" VALUES (''synthetic-migration'',''10.0.12''); CREATE TABLE "Users" ("Id" uuid PRIMARY KEY, "Email" text NOT NULL); INSERT INTO "Users" VALUES (gen_random_uuid(),''synthetic@example.invalid'');'
    Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
        "-d", "backup_source", "-c", $fixtureSql
    ) | Out-Null
    Invoke-DockerChecked @(
        "exec", $containerName, "pg_dump", "-U", "postgres", "-Fc", "-d", "backup_source",
        "-f", "/tmp/synthetic.dump"
    ) | Out-Null
    Invoke-DockerChecked @("cp", "${containerName}:/tmp/synthetic.dump", $backupPath) | Out-Null

    & (Join-Path $PSScriptRoot "Verify-PostgresBackup.ps1") `
        -BackupPath $backupPath `
        -PostgresImage $PostgresImage
}
finally {
    if ($containerStarted) {
        & docker stop $containerName *> $null
        if ($LASTEXITCODE -ne 0) {
            & docker rm --force $containerName *> $null
        }
    }
    if (Test-Path -LiteralPath $backupPath) {
        Remove-Item -LiteralPath $backupPath -Force
    }
}
