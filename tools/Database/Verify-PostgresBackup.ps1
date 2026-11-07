[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedSha256,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{14}_[A-Za-z0-9]+$')]
    [string]$ExpectedLatestMigrationId,

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
$actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedBackupPath).Hash
if (-not [string]::Equals($actualSha256, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "O SHA-256 do backup diverge do valor registrado; restauração cancelada."
}

$containerName = "vendmachines-restore-check-$([Guid]::NewGuid().ToString('N'))"
$temporaryPassword = "restore-check-$([Guid]::NewGuid().ToString('N'))"
$containerMayExist = $false

function Invoke-DockerChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao executar Docker durante a validação do backup (código $LASTEXITCODE)."
    }
    return $output
}

function Remove-TemporaryPostgresContainer {
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível confirmar a remoção do container temporário de restauração: Docker está indisponível. Nome: $containerName."
    }

    $matchingContainers = @(& docker ps --all --filter "name=^/$containerName$" --format "{{.Names}}" 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível verificar o container temporário de restauração. Nome: $containerName."
    }

    if ($matchingContainers -contains $containerName) {
        & docker rm --force $containerName *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Falha ao remover o container temporário de restauração. Nome: $containerName."
        }
    }

    $remainingContainers = @(& docker ps --all --filter "name=^/$containerName$" --format "{{.Names}}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $remainingContainers -contains $containerName) {
        throw "A remoção do container temporário de restauração não pôde ser confirmada. Nome: $containerName."
    }
}

try {
    $containerMayExist = $true
    Invoke-DockerChecked @(
        "run", "--rm", "--detach", "--network", "none", "--name", $containerName,
        "--env", "POSTGRES_PASSWORD=$temporaryPassword",
        $PostgresImage
    ) | Out-Null
    $ready = $false
    $consecutiveReadyChecks = 0
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        & docker exec $containerName pg_isready -h 127.0.0.1 -U postgres *> $null
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

    $latestMigrationId = (Invoke-DockerChecked @(
        "exec", $containerName, "psql", "--tuples-only", "--no-align", "-U", "postgres",
        "-d", "backup_restore_check", "-c", 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;'
    ) | Out-String).Trim()
    if (-not [string]::Equals($latestMigrationId, $ExpectedLatestMigrationId, [StringComparison]::Ordinal)) {
        throw "A última migration restaurada ($latestMigrationId) diverge da versão esperada ($ExpectedLatestMigrationId)."
    }

    Write-Host "Restauração validada em PostgreSQL isolado; migrations encontradas: $migrationCount; última migration: $latestMigrationId."
}
finally {
    if ($containerMayExist) {
        Remove-TemporaryPostgresContainer
    }
}
