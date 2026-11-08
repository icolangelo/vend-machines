[CmdletBinding()]
param(
    [string]$SegmentsDirectory = "artifacts",
    [string]$PostgresImage = "postgres:17-alpine",
    [ValidateRange(10, 600)]
    [int]$StartupTimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedSegmentsDirectory = (Resolve-Path -LiteralPath $SegmentsDirectory -ErrorAction Stop).Path
$segmentNames = @(
    "postgresql-migrations-before-session-index.sql",
    "postgresql-migration-session-index.sql",
    "postgresql-migrations-between-online-indexes.sql",
    "postgresql-migration-payment-reconciliation-index.sql",
    "postgresql-migrations-after-payment-index.sql",
    "postgresql-migration-inventory-mapping-index.sql",
    "postgresql-migrations-after-inventory-index.sql"
)

foreach ($segmentName in $segmentNames) {
    $segmentPath = Join-Path $resolvedSegmentsDirectory $segmentName
    if (-not (Test-Path -LiteralPath $segmentPath -PathType Leaf)) {
        throw "O segmento PostgreSQL obrigatório não existe: $segmentName."
    }

    $content = [IO.File]::ReadAllText($segmentPath)
    if (-not $content.StartsWith("SET lock_timeout = '5s';`n", [StringComparison]::Ordinal) -and
        -not $content.StartsWith("SET lock_timeout = '5s';`r`n", [StringComparison]::Ordinal)) {
        throw "O segmento $segmentName não começa com o limite de lock de cinco segundos."
    }
    if ($content.Contains([char]0xFEFF)) {
        throw "O segmento $segmentName contém BOM UTF-8 antes de um token SQL."
    }
}

$containerName = "vendmachines-release-migrations-$([Guid]::NewGuid().ToString('N'))"
$temporaryPassword = "release-fixture-$([Guid]::NewGuid().ToString('N'))"
$temporaryDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } elseif ($env:TEMP) { $env:TEMP } else { [IO.Path]::GetTempPath() }
$bootstrapFixturePath = Join-Path $temporaryDirectory "vendmachines-release-bootstrap-$([Guid]::NewGuid().ToString('N')).sql"
$queryFixturePath = Join-Path $temporaryDirectory "vendmachines-release-query-$([Guid]::NewGuid().ToString('N')).sql"
$containerStarted = $false

function Invoke-DockerChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | Select-Object -Last 20 | Out-String).Trim()
        throw "Falha no ensaio dos segmentos SQL (código $LASTEXITCODE): $detail"
    }
    return $output
}

function Invoke-PostgresScalar {
    param([Parameter(Mandatory = $true)][string]$Sql)

    [IO.File]::WriteAllText($queryFixturePath, $Sql, [Text.Encoding]::ASCII)
    Invoke-DockerChecked @("cp", $queryFixturePath, "${containerName}:/tmp/release-query.sql") | Out-Null
    $output = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "--tuples-only", "--no-align",
        "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "release_segments", "-f", "/tmp/release-query.sql"
    )
    return ($output | Out-String).Trim()
}

try {
    Invoke-DockerChecked @(
        "run", "--detach", "--network", "none", "--name", $containerName,
        "--env", "POSTGRES_PASSWORD=$temporaryPassword", $PostgresImage
    ) | Out-Null
    $containerStarted = $true

    # TCP readiness excludes the Unix-socket-only bootstrap server.
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $consecutiveReadyChecks = 0
    while ([DateTime]::UtcNow -lt $deadline) {
        & docker exec $containerName pg_isready -h 127.0.0.1 -U postgres *> $null
        if ($LASTEXITCODE -eq 0) {
            $consecutiveReadyChecks++
            if ($consecutiveReadyChecks -ge 3) { break }
        } else {
            $consecutiveReadyChecks = 0
        }
        Start-Sleep -Milliseconds 500
    }
    if ($consecutiveReadyChecks -lt 3) {
        throw "O PostgreSQL descartável não ficou pronto em $StartupTimeoutSeconds segundos."
    }

    Invoke-DockerChecked @("exec", $containerName, "createdb", "-U", "postgres", "release_segments") | Out-Null

    foreach ($segmentName in $segmentNames[0..3]) {
        $segmentPath = Join-Path $resolvedSegmentsDirectory $segmentName
        $containerPath = "/tmp/$segmentName"
        Invoke-DockerChecked @("cp", $segmentPath, "${containerName}:$containerPath") | Out-Null
        Invoke-DockerChecked @(
            "exec", $containerName, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
            "-d", "release_segments", "-f", $containerPath
        ) | Out-Null
        Write-Host "Segmento aplicado: $segmentName"
    }

    # This row exists only in the disposable fixture and satisfies the audited
    # PlatformAdmin bootstrap migration included in the final segment.
    $bootstrapFixtureSql = @'
INSERT INTO "Users"
    ("Id", "Name", "Email", "PasswordHash", "Role", "CompanyId", "CreatedAt", "Cpf", "AcceptedPrivacyPolicy", "CredentialVersion")
VALUES
    (gen_random_uuid(), 'Synthetic migration owner', 'dev.ivan@gmail.com', 'synthetic-only', 'Admin', NULL, CURRENT_TIMESTAMP, '', FALSE, 0);
'@
    # Use a SQL file because Windows PowerShell can strip identifier quotes
    # from an embedded -c argument before Docker forwards it to the container.
    [IO.File]::WriteAllText($bootstrapFixturePath, $bootstrapFixtureSql, [Text.Encoding]::ASCII)
    Invoke-DockerChecked @("cp", $bootstrapFixturePath, "${containerName}:/tmp/bootstrap-fixture.sql") | Out-Null
    Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
        "-d", "release_segments", "-f", "/tmp/bootstrap-fixture.sql"
    ) | Out-Null

    foreach ($finalSegment in $segmentNames[4..6]) {
        $finalPath = Join-Path $resolvedSegmentsDirectory $finalSegment
        $finalContainerPath = "/tmp/$finalSegment"
        Invoke-DockerChecked @("cp", $finalPath, "${containerName}:$finalContainerPath") | Out-Null
        Invoke-DockerChecked @(
            "exec", $containerName, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
            "-d", "release_segments", "-f", $finalContainerPath
        ) | Out-Null
    }

    $latestMigration = Invoke-PostgresScalar 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;'
    if ($latestMigration -ne "20261108113731_TrackMdbMenuSequence") {
        throw "A cadeia terminou em '$latestMigration', não na migration candidata esperada."
    }
    $leaseColumn = Invoke-PostgresScalar -Sql 'SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = ''MachineConnectionStates'' AND column_name = ''OwnerLeaseExpiresAt'' AND is_nullable = ''YES'';'
    if ($leaseColumn -ne "1") { throw "O último segmento não adicionou OwnerLeaseExpiresAt nullable." }
    $legacyOwnerReset = Invoke-PostgresScalar -Sql "SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname = 'clear_legacy_telemetry_owner_on_connection_change' AND pg_get_functiondef(p.oid) LIKE '%CommandRoutingVersion%NULL%' AND pg_get_functiondef(p.oid) LIKE '%OwnerLeaseExpiresAt%NULL%') THEN '1' ELSE '0' END;"
    if ($legacyOwnerReset -ne "1") { throw "O último segmento não remove metadados de roteamento obsoletos após reconexão legada." }

    $schemaChecks = [ordered]@{
        "CommandRoutingVersion" = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'MachineConnectionStates' AND column_name = 'CommandRoutingVersion';"
        "SessionAdmissionGates" = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'SessionAdmissionGates';"
        "TelemetryCommandResolutions" = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'TelemetryCommandResolutions';"
        "DeviceMessageInbox" = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'DeviceMessageInbox';"
        "WebhookInboxMessages" = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'WebhookInboxMessages';"
        "ReconciliationReason" = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'PaymentTransactions' AND column_name = 'ReconciliationReason';"
        "PrivacyPolicyVersion" = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'Users' AND column_name = 'PrivacyPolicyVersion' AND is_nullable = 'YES' AND column_default IS NULL;"
    }
    foreach ($check in $schemaChecks.GetEnumerator()) {
        $value = Invoke-PostgresScalar $check.Value
        if ($value -ne "1") { throw "A verificação de schema '$($check.Key)' retornou '$value'." }
    }

    $legacyWriterPrivacyVersion = Invoke-PostgresScalar @'
INSERT INTO "Users"
    ("Id", "Name", "Email", "PasswordHash", "Role", "CompanyId", "CreatedAt", "Cpf", "AcceptedPrivacyPolicy", "CredentialVersion")
VALUES
    (gen_random_uuid(), 'Synthetic legacy writer', 'legacy.writer@migration.invalid', 'synthetic-only', 'Operator', NULL, CURRENT_TIMESTAMP, '', FALSE, 0)
RETURNING CASE WHEN "PrivacyPolicyVersion" IS NULL THEN 'NULL' ELSE "PrivacyPolicyVersion" END;
'@
    $legacyWriterResult = ($legacyWriterPrivacyVersion -split '\s+' | Select-Object -First 1)
    if ($legacyWriterResult -ne "NULL") {
        throw "Uma gravação compatível com o binário anterior não deixou PrivacyPolicyVersion NULL: '$legacyWriterPrivacyVersion'."
    }

    $admissionGate = Invoke-PostgresScalar 'SELECT COUNT(*) FROM "SessionAdmissionGates" WHERE "Id" = 1 AND "NewSessionsPaused" = FALSE;'
    if ($admissionGate -ne "1") { throw "A migration não deixou exatamente um gate de admissão aberto por padrão: '$admissionGate'." }

    $activeSafetyTriggers = Invoke-PostgresScalar @'
SELECT COUNT(*)
FROM pg_trigger
WHERE NOT tgisinternal
  AND tgenabled = 'O'
  AND tgname IN (
      'TR_MachineSessions_RejectWhenDeploymentPaused',
      'TR_TelemetryCommandResolutions_ValidateTarget'
  );
'@
    if ($activeSafetyTriggers -ne "2") { throw "O PostgreSQL encontrou somente $activeSafetyTriggers dos dois triggers de segurança habilitados." }

    $bootstrapRole = Invoke-PostgresScalar 'SELECT COUNT(*) FROM "Users" WHERE "NormalizedEmail" = ''dev.ivan@gmail.com'' AND "Role" = ''PlatformAdmin'' AND "IsActive" = TRUE;'
    if ($bootstrapRole -ne "1") { throw "O bootstrap sintético de PlatformAdmin retornou '$bootstrapRole'." }

    $bootstrapAudit = Invoke-PostgresScalar 'SELECT COUNT(*) FROM "AuditLogs" WHERE "Action" = ''auth.platform_admin_bootstrap_completed'';'
    if ($bootstrapAudit -ne "1") { throw "A auditoria do bootstrap sintético retornou '$bootstrapAudit'." }

    $validIndexes = Invoke-PostgresScalar @'
SELECT COUNT(*)
FROM pg_index
WHERE indisvalid
  AND indexrelid IN (
      '"UX_MachineSessions_OneOpenPerMachine"'::regclass,
      '"IX_PaymentTransactions_Status_LastReconciliationAt_CreatedAt"'::regclass,
      '"IX_InventoryMovements_MappingMismatch_Machine_Item_CreatedAt"'::regclass
  );
'@
    if ($validIndexes -ne "3") { throw "O PostgreSQL encontrou somente $validIndexes dos três índices concorrentes válidos." }
    $mappingIndexPredicate = Invoke-PostgresScalar @'
SELECT CASE WHEN pg_get_indexdef(i.indexrelid) ILIKE '%("MovementType", "MachineId", "ItemNumber", "CreatedAt")%'
  AND pg_get_expr(i.indpred, i.indrelid) ILIKE '%SaleMappingMismatch%' THEN 1 ELSE 0 END
FROM pg_index i
JOIN pg_class c ON c.oid = i.indexrelid
WHERE c.relname = 'IX_InventoryMovements_MappingMismatch_Machine_Item_CreatedAt';
'@
    if ($mappingIndexPredicate -ne "1") { throw "O índice de alertas não limita entradas ao tipo SaleMappingMismatch." }

    Write-Host "Segmentos PostgreSQL da release aplicados; última migration: $latestMigration."
    Write-Host "Bootstrap sintético, auditoria, colunas, tabelas e três índices concorrentes válidos."
    Write-Host "Ensaio isolado aprovado; nenhum banco externo foi conectado."
}
finally {
    if ($containerStarted) {
        & docker rm --force $containerName *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Não foi possível remover o container descartável $containerName."
        }
    }
    if (Test-Path -LiteralPath $bootstrapFixturePath) {
        Remove-Item -LiteralPath $bootstrapFixturePath -Force
    }
    if (Test-Path -LiteralPath $queryFixturePath) {
        Remove-Item -LiteralPath $queryFixturePath -Force
    }
}
