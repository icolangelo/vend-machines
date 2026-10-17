[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupPath,

    [Parameter(Mandatory = $true)]
    [string]$PostgresImage,

    [ValidateRange(10, 600)]
    [int]$StartupTimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$backupItem = Get-Item -LiteralPath $BackupPath -ErrorAction Stop
if ($backupItem.PSIsContainer -or $backupItem.Length -le 0) {
    throw "Informe um arquivo de backup PostgreSQL customizado não vazio."
}
$resolvedBackupPath = $backupItem.FullName

$containerName = "vendmachines-restore-check-$([Guid]::NewGuid().ToString('N'))"
$temporaryPassword = "restore-check-$([Guid]::NewGuid().ToString('N'))"
$containerStarted = $false

function Invoke-DockerChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao executar Docker durante a validação do backup (código $LASTEXITCODE)."
    }
    return $output
}

try {
    Invoke-DockerChecked @(
        "run", "--rm", "--detach", "--network", "none", "--name", $containerName,
        "--env", "POSTGRES_PASSWORD=$temporaryPassword",
        $PostgresImage
    ) | Out-Null
    $containerStarted = $true

    $ready = $false
    $consecutiveReadyChecks = 0
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
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
        throw "O PostgreSQL isolado não ficou pronto dentro do limite configurado."
    }

    Invoke-DockerChecked @("cp", $resolvedBackupPath, "${containerName}:/tmp/vendmachines-backup.dump") | Out-Null
    Invoke-DockerChecked @("exec", $containerName, "pg_restore", "--list", "/tmp/vendmachines-backup.dump") | Out-Null
    Invoke-DockerChecked @("exec", $containerName, "createdb", "-U", "postgres", "backup_restore_check") | Out-Null
    Invoke-DockerChecked @(
        "exec", $containerName, "pg_restore", "--no-owner", "--no-privileges",
        "--exit-on-error", "-U", "postgres", "--dbname=backup_restore_check",
        "/tmp/vendmachines-backup.dump"
    ) | Out-Null

    $schemaCheck = (Invoke-DockerChecked @(
        "exec", $containerName, "psql", "--tuples-only", "--no-align", "-U", "postgres",
        "-d", "backup_restore_check", "-c",
        'SELECT CASE WHEN to_regclass(''public."__EFMigrationsHistory"'') IS NOT NULL AND to_regclass(''public."Users"'') IS NOT NULL THEN 1 ELSE 0 END;'
    ) | Out-String).Trim()
    if ($schemaCheck -ne "1") {
        throw "O backup foi restaurado, mas não contém o histórico de migrations e a tabela de usuários esperados."
    }

    $migrationCountText = (Invoke-DockerChecked @(
        "exec", $containerName, "psql", "--tuples-only", "--no-align", "-U", "postgres",
        "-d", "backup_restore_check", "-c", 'SELECT COUNT(*) FROM "__EFMigrationsHistory";'
    ) | Out-String).Trim()
    $migrationCount = 0
    if (-not [int]::TryParse($migrationCountText, [ref]$migrationCount) -or $migrationCount -lt 1) {
        throw "A restauração terminou sem migrations registradas no banco restaurado."
    }

    Write-Host "Restauração validada em PostgreSQL isolado; migrations encontradas: $migrationCount."
}
finally {
    if ($containerStarted) {
        & docker stop $containerName *> $null
        if ($LASTEXITCODE -ne 0) {
            & docker rm --force $containerName *> $null
        }
    }
}
