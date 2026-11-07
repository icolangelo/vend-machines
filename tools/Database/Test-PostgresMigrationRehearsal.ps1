[CmdletBinding()]
param(
    [string]$PostgresImage = "postgres:17-alpine"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$tempDirectoryCandidate = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } elseif ($env:TEMP) { $env:TEMP } else { [System.IO.Path]::GetTempPath() }
if (-not (Test-Path -LiteralPath $tempDirectoryCandidate -PathType Container)) {
    throw "O diretório temporário configurado não existe."
}
$tempDirectory = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $tempDirectoryCandidate).Path)
$fixtureContainer = "vendmachines-migration-rehearsal-fixture-$([Guid]::NewGuid().ToString('N'))"
$backupPath = Join-Path $tempDirectory "vendmachines-migration-rehearsal-$([Guid]::NewGuid().ToString('N')).dump"
$appliedBackupPath = Join-Path $tempDirectory "vendmachines-migration-applied-$([Guid]::NewGuid().ToString('N')).dump"
$invalidIndexBackupPath = Join-Path $tempDirectory "vendmachines-migration-invalid-index-$([Guid]::NewGuid().ToString('N')).dump"
$packagePath = Join-Path $tempDirectory "vendmachines-migration-package-$([Guid]::NewGuid().ToString('N'))"
$fixtureStarted = $false
$packageDirectoryCreated = $false
$packagePathSafeToRemove = $false

function Test-DirectChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ParentPath
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParentPath = [IO.Path]::GetFullPath($ParentPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $actualParentPath = [IO.Path]::GetDirectoryName($fullPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return [string]::Equals($actualParentPath, $fullParentPath, [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-DockerChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | Select-Object -Last 3 | Out-String).Trim()
        throw "Falha ao preparar o fixture sintético de migrations (código $LASTEXITCODE): $detail"
    }
    return $output
}

function Get-RehearsalContainers {
    $names = & docker ps --all --filter "name=vendmachines-migration-rehearsal-" --format "{{.Names}}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível verificar a limpeza dos containers de ensaio."
    }
    return @($names | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Invoke-RehearsalScript {
    param(
        [Parameter(Mandatory = $true)][string]$Backup,
        [Parameter(Mandatory = $true)][string]$BackupLatestMigration
    )

    $output = & (Join-Path $PSScriptRoot "Rehearse-PostgresMigrations.ps1") `
        -BackupPath $Backup `
        -ExpectedBackupSha256 (Get-FileHash -Algorithm SHA256 -LiteralPath $Backup).Hash `
        -ExpectedBackupLatestMigrationId $BackupLatestMigration `
        -ReleasePackagePath $packagePath `
        -ExpectedReleaseSha $expectedReleaseSha `
        -ExpectedCandidateLatestMigrationId "20261101221552_AddTelemetryConnectionOwnerLease" `
        -PostgresImage $PostgresImage *>&1
    return ($output | Out-String)
}

try {
    if ((Test-Path -LiteralPath $backupPath) -or
        (Test-Path -LiteralPath $appliedBackupPath) -or
        (Test-Path -LiteralPath $invalidIndexBackupPath) -or
        (Test-Path -LiteralPath $packagePath)) {
        throw "O nome aleatório do diretório/backup temporário já existe; ensaio cancelado."
    }
    New-Item -ItemType Directory -Path $packagePath | Out-Null
    $packageDirectoryCreated = $true
    $packagePath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $packagePath).Path)
    if (-not (Test-DirectChildPath -Path $packagePath -ParentPath $tempDirectory)) {
        throw "O pacote temporário não foi criado como filho direto do diretório temporário."
    }
    $packagePathSafeToRemove = $true

    Invoke-DockerChecked @(
        "run", "--rm", "--detach", "--network", "none", "--name", $fixtureContainer,
        "--env", "POSTGRES_PASSWORD=migration-rehearsal-fixture-only", $PostgresImage
    ) | Out-Null
    $fixtureStarted = $true

    $ready = $false
    $consecutiveReadyChecks = 0
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        & docker exec $fixtureContainer pg_isready -h 127.0.0.1 -U postgres *> $null
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

    Invoke-DockerChecked @("exec", $fixtureContainer, "createdb", "-U", "postgres", "migration_source") | Out-Null
    $fixtureSql = @'
CREATE TABLE "__EFMigrationsHistory" (
    "MigrationId" character varying(150) PRIMARY KEY,
    "ProductVersion" character varying(32) NOT NULL
);
INSERT INTO "__EFMigrationsHistory" VALUES ('20260501000000_SyntheticBase', '10.0.12');
CREATE TABLE "MachineSessions" (
    "Id" uuid PRIMARY KEY,
    "MachineId" text NOT NULL,
    "ClosedAt" timestamp with time zone NULL
);
CREATE TABLE "PaymentTransactions" (
    "Id" uuid PRIMARY KEY,
    "Status" text NOT NULL,
    "LastReconciliationAt" timestamp with time zone NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);
CREATE TABLE "InventoryMovements" (
    "MovementType" text NOT NULL,
    "MachineId" text NOT NULL,
    "ItemNumber" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);
CREATE TABLE "MachineConnectionStates" (
    "MachineId" text PRIMARY KEY
);
'@
    Invoke-DockerChecked @(
        "exec", $fixtureContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
        "-d", "migration_source", "-c", $fixtureSql
    ) | Out-Null
    Invoke-DockerChecked @(
        "exec", $fixtureContainer, "pg_dump", "-U", "postgres", "-Fc", "-d", "migration_source",
        "-f", "/tmp/migration-source.dump"
    ) | Out-Null
    Invoke-DockerChecked @("cp", "${fixtureContainer}:/tmp/migration-source.dump", $backupPath) | Out-Null

    $sessionMigration = "20260730120000_AddMdbStatusAndAutomaticSession"
    $paymentMigration = "20260926174359_AddPaymentReconciliationTracking"
    $mappingMigration = "20261026091223_IndexInventoryMappingMismatchAlerts"
    $leaseMigration = "20261101221552_AddTelemetryConnectionOwnerLease"
    $scripts = [ordered]@{
        "postgresql-migrations-before-session-index.sql" = @'
SET lock_timeout = '5s';
INSERT INTO "__EFMigrationsHistory" VALUES ('20260601000000_SyntheticBeforeSession', '10.0.12');
'@
        "postgresql-migration-session-index.sql" = @"
SET lock_timeout = '5s';
CREATE UNIQUE INDEX CONCURRENTLY "UX_MachineSessions_OneOpenPerMachine" ON "MachineSessions" ("MachineId") WHERE "ClosedAt" IS NULL;
INSERT INTO "__EFMigrationsHistory" VALUES ('$sessionMigration', '10.0.12');
"@
        "postgresql-migrations-between-online-indexes.sql" = @'
SET lock_timeout = '5s';
INSERT INTO "__EFMigrationsHistory" VALUES ('20260926170000_SyntheticBetween', '10.0.12');
'@
        "postgresql-migration-payment-reconciliation-index.sql" = @"
SET lock_timeout = '5s';
CREATE INDEX CONCURRENTLY "IX_PaymentTransactions_Status_LastReconciliationAt_CreatedAt" ON "PaymentTransactions" ("Status", "LastReconciliationAt", "CreatedAt");
INSERT INTO "__EFMigrationsHistory" VALUES ('$paymentMigration', '10.0.12');
"@
        "postgresql-migrations-after-payment-index.sql" = @"
SET lock_timeout = '5s';
INSERT INTO "__EFMigrationsHistory" VALUES ('20261026083333_AddSessionProductSnapshot', '10.0.12');
"@
        "postgresql-migration-inventory-mapping-index.sql" = @"
SET lock_timeout = '5s';
CREATE INDEX CONCURRENTLY "IX_InventoryMovements_MappingMismatch_Machine_Item_CreatedAt" ON "InventoryMovements" ("MovementType", "MachineId", "ItemNumber", "CreatedAt") WHERE "MovementType" = 'SaleMappingMismatch';
INSERT INTO "__EFMigrationsHistory" VALUES ('$mappingMigration', '10.0.12');
"@
        "postgresql-migrations-after-inventory-index.sql" = @"
SET lock_timeout = '5s';
ALTER TABLE "MachineConnectionStates" ADD COLUMN IF NOT EXISTS "OwnerLeaseExpiresAt" timestamp with time zone;
INSERT INTO "__EFMigrationsHistory" VALUES ('$leaseMigration', '10.0.12') ON CONFLICT DO NOTHING;
"@
    }

    foreach ($scriptName in $scripts.Keys) {
        Write-Utf8NoBom -Path (Join-Path $packagePath $scriptName) -Content $scripts[$scriptName]
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Rehearse-PostgresMigrations.ps1") `
        -Destination (Join-Path $packagePath "Rehearse-PostgresMigrations.ps1")

    $expectedReleaseSha = "a" * 40
    $releaseShaPath = Join-Path $packagePath "source-sha.txt"
    Write-Utf8NoBom -Path $releaseShaPath -Content $expectedReleaseSha
    $manifest = Get-ChildItem -LiteralPath $packagePath -File | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
    [IO.File]::WriteAllLines((Join-Path $packagePath "SHA256SUMS"), [string[]]$manifest, [Text.Encoding]::ASCII)

    $existingRehearsalContainers = @(Get-RehearsalContainers)
    $freshOutput = Invoke-RehearsalScript -Backup $backupPath -BackupLatestMigration "20260501000000_SyntheticBase"
    Write-Host $freshOutput.Trim()
    if ($freshOutput -notlike "*Ensaio isolado aprovado*") {
        throw "A restauração sintética inicial não concluiu a cadeia candidata."
    }

    $remainingRehearsalContainers = @(Get-RehearsalContainers)
    $unexpectedContainers = @($remainingRehearsalContainers | Where-Object { $_ -notin $existingRehearsalContainers })
    if ($unexpectedContainers.Count -gt 0) {
        throw "O ensaio deixou container temporário ativo: $($unexpectedContainers -join ', ')."
    }

    $appliedFixtureSql = @"
CREATE UNIQUE INDEX "UX_MachineSessions_OneOpenPerMachine" ON "MachineSessions" ("MachineId") WHERE "ClosedAt" IS NULL;
INSERT INTO "__EFMigrationsHistory" VALUES ('$sessionMigration', '10.0.12');
CREATE INDEX "IX_PaymentTransactions_Status_LastReconciliationAt_CreatedAt" ON "PaymentTransactions" ("Status", "LastReconciliationAt", "CreatedAt");
INSERT INTO "__EFMigrationsHistory" VALUES ('$paymentMigration', '10.0.12');
CREATE INDEX "IX_InventoryMovements_MappingMismatch_Machine_Item_CreatedAt" ON "InventoryMovements" ("MovementType", "MachineId", "ItemNumber", "CreatedAt") WHERE "MovementType" = 'SaleMappingMismatch';
INSERT INTO "__EFMigrationsHistory" VALUES ('$mappingMigration', '10.0.12');
ALTER TABLE "MachineConnectionStates" ADD COLUMN "OwnerLeaseExpiresAt" timestamp with time zone;
INSERT INTO "__EFMigrationsHistory" VALUES ('$leaseMigration', '10.0.12');
"@
    Invoke-DockerChecked @(
        "exec", $fixtureContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
        "-d", "migration_source", "-c", $appliedFixtureSql
    ) | Out-Null
    Invoke-DockerChecked @(
        "exec", $fixtureContainer, "pg_dump", "-U", "postgres", "-Fc", "-d", "migration_source",
        "-f", "/tmp/migration-applied.dump"
    ) | Out-Null
    Invoke-DockerChecked @("cp", "${fixtureContainer}:/tmp/migration-applied.dump", $appliedBackupPath) | Out-Null

    $alreadyAppliedOutput = Invoke-RehearsalScript `
        -Backup $appliedBackupPath `
        -BackupLatestMigration $leaseMigration
    Write-Host $alreadyAppliedOutput.Trim()
    if ($alreadyAppliedOutput -notlike "*Segmento do índice de sessão ignorado*" -or
        $alreadyAppliedOutput -notlike "*Segmento do índice de pagamentos ignorado*" -or
        $alreadyAppliedOutput -notlike "*Segmento do índice de alertas de estoque ignorado*" -or
        $alreadyAppliedOutput -notlike "*Ensaio isolado aprovado*") {
        throw "O ensaio não pulou corretamente migrations já aplicadas com índices válidos."
    }

    Invoke-DockerChecked @(
        "exec", $fixtureContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
        "-d", "migration_source", "-c", 'DROP INDEX "UX_MachineSessions_OneOpenPerMachine";'
    ) | Out-Null
    Invoke-DockerChecked @(
        "exec", $fixtureContainer, "pg_dump", "-U", "postgres", "-Fc", "-d", "migration_source",
        "-f", "/tmp/migration-invalid-index.dump"
    ) | Out-Null
    Invoke-DockerChecked @("cp", "${fixtureContainer}:/tmp/migration-invalid-index.dump", $invalidIndexBackupPath) | Out-Null

    $containersBeforeInvalidIndexCheck = @(Get-RehearsalContainers)
    $invalidIndexRejected = $false
    try {
        Invoke-RehearsalScript -Backup $invalidIndexBackupPath -BackupLatestMigration $leaseMigration | Out-Null
    }
    catch {
        $invalidIndexRejected = $_.Exception.Message -like "*migration de sessão consta no histórico, mas o índice não está válido*"
        if (-not $invalidIndexRejected) { throw }
    }
    if (-not $invalidIndexRejected) {
        throw "O ensaio não falhou fechado quando o histórico indicava uma migration sem seu índice."
    }
    $containersAfterInvalidIndexCheck = @(Get-RehearsalContainers)
    $containersCreatedForInvalidIndex = @($containersAfterInvalidIndexCheck | Where-Object { $_ -notin $containersBeforeInvalidIndexCheck })
    if ($containersCreatedForInvalidIndex.Count -gt 0) {
        throw "O ensaio deixou container após detectar migration aplicada sem índice válido."
    }
    Write-Host "Migration registrada sem índice válido rejeitada; container temporário removido."

    $sessionScriptPath = Join-Path $packagePath "postgresql-migration-session-index.sql"
    $originalSessionScript = Get-Content -LiteralPath $sessionScriptPath -Raw
    Write-Utf8NoBom -Path $sessionScriptPath -Content ($originalSessionScript + "`n-- altered after checksum generation")
    $containersBeforeTamperCheck = @(Get-RehearsalContainers)
    $tamperedPackageRejected = $false
    try {
        Invoke-RehearsalScript -Backup $backupPath -BackupLatestMigration "20260501000000_SyntheticBase" | Out-Null
    }
    catch {
        $tamperedPackageRejected = $_.Exception.Message -like "*Checksum inválido no pacote*"
        if (-not $tamperedPackageRejected) { throw }
    }
    if (-not $tamperedPackageRejected) {
        throw "O ensaio não rejeitou o segmento alterado após a geração do checksum."
    }
    $containersAfterTamperCheck = @(Get-RehearsalContainers)
    $containersCreatedBeforeRestore = @($containersAfterTamperCheck | Where-Object { $_ -notin $containersBeforeTamperCheck })
    if ($containersCreatedBeforeRestore.Count -gt 0) {
        throw "A validação do checksum alterado criou containers antes de rejeitar o pacote."
    }
    Write-Host "Segmento alterado rejeitado pelo checksum antes da restauração."

    Write-Host "O ensaio sintético de migrations passou e removeu os containers temporários."
}
finally {
    if ($fixtureStarted) {
        & docker stop $fixtureContainer *> $null
        if ($LASTEXITCODE -ne 0) {
            & docker rm --force $fixtureContainer *> $null
        }
    }
    foreach ($temporaryBackup in @($backupPath, $appliedBackupPath, $invalidIndexBackupPath)) {
        if ((Test-Path -LiteralPath $temporaryBackup) -and (Test-DirectChildPath -Path $temporaryBackup -ParentPath $tempDirectory)) {
            Remove-Item -LiteralPath $temporaryBackup -Force
        }
    }
    if ($packageDirectoryCreated -and $packagePathSafeToRemove -and (Test-Path -LiteralPath $packagePath -PathType Container)) {
        Remove-Item -LiteralPath $packagePath -Recurse -Force
    }
}
