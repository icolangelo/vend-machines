[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedBackupSha256,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{14}_[A-Za-z0-9_]+$')]
    [string]$ExpectedBackupLatestMigrationId,

    [Parameter(Mandatory = $true)]
    [string]$ReleasePackagePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{40}([A-Fa-f0-9]{24})?$')]
    [string]$ExpectedReleaseSha,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{14}_[A-Za-z0-9_]+$')]
    [string]$ExpectedCandidateLatestMigrationId,

    [string]$PostgresImage = "postgres:17-alpine",

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
$actualBackupSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedBackupPath).Hash
if (-not [string]::Equals($actualBackupSha256, $ExpectedBackupSha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "O SHA-256 do backup diverge do valor registrado; ensaio cancelado."
}

$resolvedPackagePath = (Resolve-Path -LiteralPath $ReleasePackagePath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $resolvedPackagePath -PathType Container)) {
    throw "Informe o diretório do pacote de release validado."
}
$releaseShaPath = Join-Path $resolvedPackagePath "source-sha.txt"
$checksumsPath = Join-Path $resolvedPackagePath "SHA256SUMS"
if (-not (Test-Path -LiteralPath $releaseShaPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $checksumsPath -PathType Leaf)) {
    throw "O pacote precisa conter source-sha.txt e SHA256SUMS."
}

$packageSha = (Get-Content -LiteralPath $releaseShaPath -Raw).Trim()
if (-not [string]::Equals($packageSha, $ExpectedReleaseSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "O SHA do pacote diverge da release esperada; ensaio cancelado."
}

$checksumEntries = @{}
foreach ($line in Get-Content -LiteralPath $checksumsPath) {
    if ($line -notmatch '^([A-Fa-f0-9]{64})\s+\*?([^\\/\s]+)$') {
        throw "Entrada inválida em SHA256SUMS; ensaio cancelado."
    }

    $expectedHash = $matches[1]
    $fileName = $matches[2]
    if ($fileName -in @(".", "..") -or $checksumEntries.ContainsKey($fileName)) {
        throw "Nome duplicado ou inválido em SHA256SUMS; ensaio cancelado."
    }

    $packageFile = Join-Path $resolvedPackagePath $fileName
    $fileItem = Get-Item -LiteralPath $packageFile -ErrorAction Stop
    if ($fileItem.PSIsContainer -or ($fileItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "O pacote contém caminho não regular; ensaio cancelado."
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $packageFile).Hash
    if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Checksum inválido no pacote; ensaio cancelado."
    }
    $checksumEntries[$fileName] = $true
}
if (-not $checksumEntries.ContainsKey("source-sha.txt")) {
    throw "SHA256SUMS não protege source-sha.txt; ensaio cancelado."
}

$scriptNames = @(
    "postgresql-migrations-before-session-index.sql",
    "postgresql-migration-session-index.sql",
    "postgresql-migrations-between-online-indexes.sql",
    "postgresql-migration-payment-reconciliation-index.sql",
    "postgresql-migrations-after-payment-index.sql",
    "postgresql-migration-inventory-mapping-index.sql",
    "postgresql-migrations-after-inventory-index.sql"
)
foreach ($scriptName in $scriptNames) {
    $scriptPath = Join-Path $resolvedPackagePath $scriptName
    if (-not $checksumEntries.ContainsKey($scriptName) -or -not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "O pacote não contém os sete segmentos PostgreSQL verificados; ensaio cancelado."
    }
    $scriptContent = Get-Content -LiteralPath $scriptPath -Raw
    if (-not $scriptContent.StartsWith("SET lock_timeout = '5s';`n", [StringComparison]::Ordinal) -and
        -not $scriptContent.StartsWith("SET lock_timeout = '5s';`r`n", [StringComparison]::Ordinal)) {
        throw "O segmento $scriptName não começa com o limite de lock esperado."
    }
}

$containerName = "vendmachines-migration-rehearsal-$([Guid]::NewGuid().ToString('N'))"
$temporaryPassword = "rehearsal-$([Guid]::NewGuid().ToString('N'))"
$containerMayExist = $false
$totalTimer = [Diagnostics.Stopwatch]::StartNew()

function Invoke-DockerChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao executar o ensaio de migrations isolado (código $LASTEXITCODE)."
    }
    return $output
}

function Remove-TemporaryPostgresContainer {
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível confirmar a remoção do container temporário do ensaio de migrations: Docker está indisponível. Nome: $containerName."
    }

    $matchingContainers = @(& docker ps --all --filter "name=^/$containerName$" --format "{{.Names}}" 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível verificar o container temporário do ensaio de migrations. Nome: $containerName."
    }

    if ($matchingContainers -contains $containerName) {
        & docker rm --force $containerName *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Falha ao remover o container temporário do ensaio de migrations. Nome: $containerName."
        }
    }

    $remainingContainers = @(& docker ps --all --filter "name=^/$containerName$" --format "{{.Names}}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $remainingContainers -contains $containerName) {
        throw "A remoção do container temporário do ensaio de migrations não pôde ser confirmada. Nome: $containerName."
    }
}

function Invoke-RehearsalPsql {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    return Invoke-DockerChecked (@(
        "exec", $containerName, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
        "-d", "migration_rehearsal"
    ) + $Arguments)
}

function Get-RehearsalScalar {
    param([Parameter(Mandatory = $true)][string]$Sql)

    return (Invoke-RehearsalPsql @("--tuples-only", "--no-align", "-c", $Sql) | Out-String).Trim()
}

function Test-RehearsalMigration {
    param([Parameter(Mandatory = $true)][string]$MigrationId)

    $sql = 'SELECT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = ''' + $MigrationId + ''');'
    return (Get-RehearsalScalar -Sql $sql) -eq "t"
}

function Test-RehearsalIndex {
    param([Parameter(Mandatory = $true)][string]$IndexName)

    $sql = "SELECT EXISTS (SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relname = '$IndexName' AND i.indisvalid AND i.indisready);"
    return (Get-RehearsalScalar -Sql $sql) -eq "t"
}

function Invoke-RehearsalSegment {
    param([Parameter(Mandatory = $true)][string]$ScriptName)

    $scriptPath = Join-Path $resolvedPackagePath $ScriptName
    $containerScriptPath = "/tmp/$ScriptName"
    Invoke-DockerChecked @("cp", $scriptPath, "${containerName}:$containerScriptPath") | Out-Null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Invoke-RehearsalPsql @("-f", $containerScriptPath) | Out-Null
    $timer.Stop()
    Write-Host ("Segmento aplicado: {0}; duração: {1:N2}s" -f $ScriptName, $timer.Elapsed.TotalSeconds)
}

try {
    $containerMayExist = $true
    Invoke-DockerChecked @(
        "run", "--rm", "--detach", "--network", "none", "--name", $containerName,
        "--env", "POSTGRES_PASSWORD=$temporaryPassword", $PostgresImage
    ) | Out-Null
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

    Invoke-DockerChecked @("cp", $resolvedBackupPath, "${containerName}:/tmp/backup.dump") | Out-Null
    Invoke-DockerChecked @("exec", $containerName, "pg_restore", "--list", "/tmp/backup.dump") | Out-Null
    Invoke-DockerChecked @("exec", $containerName, "createdb", "-U", "postgres", "migration_rehearsal") | Out-Null
    Invoke-DockerChecked @(
        "exec", $containerName, "pg_restore", "--no-owner", "--no-privileges", "--exit-on-error",
        "-U", "postgres", "--dbname=migration_rehearsal", "/tmp/backup.dump"
    ) | Out-Null

    $schemaCheck = Get-RehearsalScalar -Sql 'SELECT CASE WHEN to_regclass(''public."__EFMigrationsHistory"'') IS NOT NULL THEN 1 ELSE 0 END;'
    if ($schemaCheck -ne "1") {
        throw "O backup restaurado não contém __EFMigrationsHistory."
    }
    $baseLatestMigrationId = Get-RehearsalScalar -Sql 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;'
    if (-not [string]::Equals($baseLatestMigrationId, $ExpectedBackupLatestMigrationId, [StringComparison]::Ordinal)) {
        throw "A última migration do backup diverge da versão registrada; ensaio cancelado."
    }

    Invoke-RehearsalSegment -ScriptName "postgresql-migrations-before-session-index.sql"

    $sessionIndexMigration = "20260730120000_AddMdbStatusAndAutomaticSession"
    if (Test-RehearsalMigration -MigrationId $sessionIndexMigration) {
        if (-not (Test-RehearsalIndex -IndexName "UX_MachineSessions_OneOpenPerMachine")) {
            throw "A migration de sessão consta no histórico, mas o índice não está válido."
        }
        Write-Host "Segmento do índice de sessão ignorado: migration já aplicada e índice válido."
    } else {
        Invoke-RehearsalSegment -ScriptName "postgresql-migration-session-index.sql"
    }

    Invoke-RehearsalSegment -ScriptName "postgresql-migrations-between-online-indexes.sql"

    $paymentIndexMigration = "20260926174359_AddPaymentReconciliationTracking"
    if (Test-RehearsalMigration -MigrationId $paymentIndexMigration) {
        if (-not (Test-RehearsalIndex -IndexName "IX_PaymentTransactions_Status_LastReconciliationAt_CreatedAt")) {
            throw "A migration de reconciliação consta no histórico, mas o índice não está válido."
        }
        Write-Host "Segmento do índice de pagamentos ignorado: migration já aplicada e índice válido."
    } else {
        Invoke-RehearsalSegment -ScriptName "postgresql-migration-payment-reconciliation-index.sql"
    }

    Invoke-RehearsalSegment -ScriptName "postgresql-migrations-after-payment-index.sql"

    $inventoryMappingMigration = "20261026091223_IndexInventoryMappingMismatchAlerts"
    if (Test-RehearsalMigration -MigrationId $inventoryMappingMigration) {
        if (-not (Test-RehearsalIndex -IndexName "IX_InventoryMovements_MappingMismatch_Machine_Item_CreatedAt")) {
            throw "A migration de alertas de estoque consta no histórico, mas o índice não está válido."
        }
        $mappingPredicate = Get-RehearsalScalar -Sql @'
SELECT EXISTS (
    SELECT 1 FROM pg_index i
    JOIN pg_class c ON c.oid = i.indexrelid
    WHERE c.relname = 'IX_InventoryMovements_MappingMismatch_Machine_Item_CreatedAt'
      AND pg_get_indexdef(i.indexrelid) ILIKE '%("MovementType", "MachineId", "ItemNumber", "CreatedAt")%'
      AND pg_get_expr(i.indpred, i.indrelid) ILIKE '%SaleMappingMismatch%'
);
'@
        if ($mappingPredicate -ne "t") {
            throw "O índice de alertas de estoque não filtra SaleMappingMismatch."
        }
        Write-Host "Segmento do índice de alertas de estoque ignorado: migration já aplicada e índice válido."
    } else {
        Invoke-RehearsalSegment -ScriptName "postgresql-migration-inventory-mapping-index.sql"
    }

    Invoke-RehearsalSegment -ScriptName "postgresql-migrations-after-inventory-index.sql"

    $candidateLatestMigrationId = Get-RehearsalScalar -Sql 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;'
    if (-not [string]::Equals($candidateLatestMigrationId, $ExpectedCandidateLatestMigrationId, [StringComparison]::Ordinal)) {
        throw "A última migration após o ensaio diverge da release esperada."
    }

    foreach ($indexName in @("UX_MachineSessions_OneOpenPerMachine", "IX_PaymentTransactions_Status_LastReconciliationAt_CreatedAt", "IX_InventoryMovements_MappingMismatch_Machine_Item_CreatedAt")) {
        if (-not (Test-RehearsalIndex -IndexName $indexName)) {
            throw "O índice esperado $indexName está ausente ou inválido após o ensaio."
        }
    }

    Write-Host "Ensaio isolado aprovado. Backup base: $baseLatestMigrationId; release: $packageSha; última migration: $candidateLatestMigrationId."
}
finally {
    try {
        if ($containerMayExist) {
            Remove-TemporaryPostgresContainer
        }
    }
    finally {
        $totalTimer.Stop()
        Write-Host ("Duração total do ensaio: {0:N2}s" -f $totalTimer.Elapsed.TotalSeconds)
    }
}
