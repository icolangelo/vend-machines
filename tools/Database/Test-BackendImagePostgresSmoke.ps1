[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Image,

    [string]$SegmentsDirectory = "artifacts",
    [string]$PostgresImage = "postgres:17-alpine",

    [ValidateRange(30, 600)]
    [int]$StartupTimeoutSeconds = 150
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
        throw "O segmento SQL exigido não existe: $segmentName."
    }

    $content = [IO.File]::ReadAllText($segmentPath)
    if (-not $content.StartsWith("SET lock_timeout = '5s';`n", [StringComparison]::Ordinal) -and
        -not $content.StartsWith("SET lock_timeout = '5s';`r`n", [StringComparison]::Ordinal)) {
        throw "O segmento SQL não começa com o limite de lock de cinco segundos: $segmentName."
    }
}

$imageInspection = & docker image inspect $Image 2>$null
if ($LASTEXITCODE -ne 0 -or -not $imageInspection) {
    throw "A imagem backend local não existe: $Image."
}

$runId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
$networkName = "vendmachines-backend-smoke-net-$runId"
$postgresContainer = "vendmachines-backend-smoke-pg-$runId"
$apiContainer = "vendmachines-backend-smoke-api-$runId"
$pendingApiContainer = "vendmachines-backend-smoke-pending-api-$runId"
$simulatorContainer = "vendmachines-backend-smoke-simulator-$runId"
$simulatorMachineId = "local-smoke-telemetry-machine"
$simulatorSerial = "LOCAL-SMOKE-TELEMETRY-$runId".ToUpperInvariant()
$legacyMachineId = "local-smoke-legacy-machine"
$legacyConnectionId = "local-smoke-legacy-socket-$runId"
$postgresPassword = "local-readiness-pg-$runId-Only"
$tlsDirectory = Join-Path ([IO.Path]::GetTempPath()) "vendmachines-backend-smoke-tls-$runId"
$tlsKeyPath = Join-Path $tlsDirectory "server.key"
$tlsCertificatePath = Join-Path $tlsDirectory "server.crt"
$overridePath = Join-Path $tlsDirectory "postgresql-migrations-after-payment-index.sql"
$simulatorCreated = $false
$networkCreated = $false
$postgresCreated = $false
$apiCreated = $false
$rsa = $null
$certificate = $null

function Invoke-DockerChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $context = $Arguments[0]
        if ($Arguments[0] -eq "exec") {
            $commandIndex = if ($Arguments.Count -gt 1 -and $Arguments[1] -eq "--user") { 4 } else { 2 }
            if ($Arguments.Count -gt $commandIndex) {
                $context = "exec $($Arguments[$commandIndex])"
            }
        }
        $detail = ($output | Select-Object -Last 5 | Out-String).Trim()
        $detail = [regex]::Replace($detail, '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '[email-redacted]')
        throw "Falha no ensaio local da imagem backend ($context): $detail"
    }
    return $output
}

function Remove-DockerContainerIfPresent {
    param([Parameter(Mandatory = $true)][string]$Name)

    & docker container inspect $Name *> $null
    if ($LASTEXITCODE -eq 0) {
        & docker rm --force $Name *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Não foi possível remover o container temporário: $Name."
        }
    }
}

function Get-ContainerHttpResponse {
    param([Parameter(Mandatory = $true)][string]$Path)

    $requestCommand = "exec 3<>/dev/tcp/127.0.0.1/8080; printf 'GET $Path HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3; cat <&3"
    return (Invoke-DockerChecked @("exec", $apiContainer, "bash", "-ec", $requestCommand) | Out-String)
}

try {
    if (Test-Path -LiteralPath $tlsDirectory) {
        throw "O diretório temporário TLS já existe: $tlsDirectory."
    }

    New-Item -ItemType Directory -Path $tlsDirectory | Out-Null
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=readiness-postgres",
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate = $request.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddMinutes(-1),
        [DateTimeOffset]::UtcNow.AddHours(1))
    [IO.File]::WriteAllText($tlsCertificatePath, $certificate.ExportCertificatePem(), [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText($tlsKeyPath, $rsa.ExportPkcs8PrivateKeyPem(), [Text.Encoding]::ASCII)

    Invoke-DockerChecked @("network", "create", "--driver", "bridge", "--internal", $networkName) | Out-Null
    $networkCreated = $true
    Invoke-DockerChecked @(
        "run", "--detach", "--network", $networkName, "--network-alias", "readiness-postgres",
        "--name", $postgresContainer,
        "--env", "POSTGRES_PASSWORD=$postgresPassword",
        "--env", "POSTGRES_DB=vendmachines",
        $PostgresImage
    ) | Out-Null
    $postgresCreated = $true

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $postgresReady = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        # O servidor temporário do bootstrap só aceita socket Unix; TCP aguarda o servidor definitivo.
        & docker exec $postgresContainer pg_isready -h 127.0.0.1 -U postgres -d vendmachines *> $null
        if ($LASTEXITCODE -eq 0) {
            $postgresReady = $true
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $postgresReady) {
        throw "O PostgreSQL descartável não ficou pronto em $StartupTimeoutSeconds segundos."
    }

    Invoke-DockerChecked @("cp", $tlsKeyPath, "${postgresContainer}:/tmp/server.key") | Out-Null
    Invoke-DockerChecked @("cp", $tlsCertificatePath, "${postgresContainer}:/tmp/server.crt") | Out-Null
    $installCertificate = "chown postgres:postgres /tmp/server.key /tmp/server.crt && chmod 600 /tmp/server.key && chmod 644 /tmp/server.crt && mv /tmp/server.key /var/lib/postgresql/data/server.key && mv /tmp/server.crt /var/lib/postgresql/data/server.crt"
    Invoke-DockerChecked @("exec", "--user", "root", $postgresContainer, "sh", "-c", $installCertificate) | Out-Null
    Invoke-DockerChecked @("exec", $postgresContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "vendmachines", "-c", "ALTER SYSTEM SET ssl = 'on';") | Out-Null
    Invoke-DockerChecked @("exec", $postgresContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "vendmachines", "-c", "SELECT pg_reload_conf();") | Out-Null
    $sslState = (Invoke-DockerChecked @("exec", $postgresContainer, "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines", "-c", "SHOW ssl;") | Out-String).Trim()
    if ($sslState -ne "on") {
        throw "TLS não foi habilitado no PostgreSQL descartável."
    }

    $migrationSegmentOverrides = @{}
    $bootstrapMigrationId = "20260927163720_BootstrapPlatformAdmin"
    $bootstrapSegmentName = "postgresql-migrations-after-payment-index.sql"
    $bootstrapSegmentPath = Join-Path $resolvedSegmentsDirectory $bootstrapSegmentName
    $bootstrapSegment = [IO.File]::ReadAllText($bootstrapSegmentPath)
    $bootstrapMigrationOffset = $bootstrapSegment.IndexOf($bootstrapMigrationId, [StringComparison]::Ordinal)
    $bootstrapSecondOffset = if ($bootstrapMigrationOffset -ge 0) {
        $bootstrapSegment.IndexOf($bootstrapMigrationId, $bootstrapMigrationOffset + 1, [StringComparison]::Ordinal)
    }
    else {
        -1
    }
    $bootstrapThirdOffset = if ($bootstrapSecondOffset -ge 0) {
        $bootstrapSegment.IndexOf($bootstrapMigrationId, $bootstrapSecondOffset + 1, [StringComparison]::Ordinal)
    }
    else {
        -1
    }
    if ($bootstrapMigrationOffset -lt 0 -or $bootstrapSecondOffset -lt 0 -or $bootstrapThirdOffset -lt 0 -or
        $bootstrapSegment.IndexOf($bootstrapMigrationId, $bootstrapThirdOffset + 1, [StringComparison]::Ordinal) -ge 0) {
        throw "Não foi possível identificar a transação da migration de bootstrap no segmento PostgreSQL."
    }
    $bootstrapTransactionOffset = $bootstrapSegment.LastIndexOf("START TRANSACTION;", $bootstrapMigrationOffset, [StringComparison]::Ordinal)
    if ($bootstrapTransactionOffset -lt 0) {
        throw "Não foi possível localizar o início da transação da migration de bootstrap."
    }

    $migrationPath = Join-Path $PSScriptRoot "../../apps/backend/VendingMachines.Api/Migrations/20260927163720_BootstrapPlatformAdmin.cs"
    $migrationSource = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $migrationPath).Path)
    $reservedEmailMatches = [regex]::Matches($migrationSource, '"NormalizedEmail"\s*=\s*''([^'']+)''')
    $reservedEmails = @($reservedEmailMatches | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    if ($reservedEmails.Count -ne 1 -or $reservedEmailMatches.Count -lt 2) {
        throw "A migration de bootstrap não contém um identificador reservado único para o ensaio descartável."
    }
    $reservedEmailSql = $reservedEmails[0].Replace("'", "''")
    $syntheticAccountSql = @"
INSERT INTO "Users"
    ("Id", "Name", "Email", "NormalizedEmail", "PasswordHash", "Role", "IsActive", "CredentialVersion", "CompanyId", "CreatedAt", "Cpf", "NormalizedCpf")
VALUES
    (gen_random_uuid(), 'Local smoke bootstrap account', '$reservedEmailSql', '$reservedEmailSql', 'synthetic-invalid-password-hash', 'Admin', TRUE, 0, NULL, CURRENT_TIMESTAMP, '', '');

"@
    $overriddenSegment = $bootstrapSegment.Insert($bootstrapTransactionOffset, $syntheticAccountSql)
    [IO.File]::WriteAllText($overridePath, $overriddenSegment, [Text.UTF8Encoding]::new($false))
    $migrationSegmentOverrides[$bootstrapSegmentName] = $overridePath

    foreach ($segmentName in $segmentNames) {
        $segmentPath = if ($migrationSegmentOverrides.ContainsKey($segmentName)) {
            $migrationSegmentOverrides[$segmentName]
        }
        else {
            Join-Path $resolvedSegmentsDirectory $segmentName
        }
        Invoke-DockerChecked @("cp", $segmentPath, "${postgresContainer}:/tmp/$segmentName") | Out-Null
        Invoke-DockerChecked @(
            "exec", $postgresContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1",
            "-U", "postgres", "-d", "vendmachines", "-f", "/tmp/$segmentName"
        ) | Out-Null
    }

    $migrationCount = (Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines",
        "-c", 'SELECT COUNT(*) FROM "__EFMigrationsHistory";'
    ) | Out-String).Trim()
    if ([int]$migrationCount -lt 1) {
        throw "O histórico de migrations não foi encontrado depois de aplicar os sete segmentos."
    }

    $syntheticCompanyId = [Guid]::NewGuid().ToString()
    $telemetryFixtureSql = @"
INSERT INTO "Companies"
    ("Id", "Name", "CreatedByUserId", "CreatedAt", "Cnpj", "NormalizedCnpj", "ActivationStatus")
SELECT '$syntheticCompanyId'::uuid, 'Local smoke telemetry company', "Id", CURRENT_TIMESTAMP,
       '00.000.000/0000-00', '00000000000000', 'Active'
FROM "Users"
WHERE "IsActive" = TRUE
LIMIT 1;

INSERT INTO "Machines"
    ("Id", "Name", "ClientName", "Location", "Status", "IsActive", "StockLevel", "Revenue30d",
     "TotalSales30d", "LastSync", "SerialNumber", "NormalizedSerialNumber", "CompanyId",
     "MercadoPagoEnabled", "AutoOpenSessionEnabled")
VALUES
    ('$simulatorMachineId', 'Local smoke telemetry machine', 'Synthetic client', 'Synthetic location',
     'offline', TRUE, 0, 0, 0, '', '$simulatorSerial', '$simulatorSerial', '$syntheticCompanyId', FALSE, FALSE);

INSERT INTO "Machines"
    ("Id", "Name", "ClientName", "Location", "Status", "IsActive", "StockLevel", "Revenue30d",
     "TotalSales30d", "LastSync", "SerialNumber", "NormalizedSerialNumber", "CompanyId",
     "MercadoPagoEnabled", "AutoOpenSessionEnabled")
VALUES
    ('$legacyMachineId', 'Local smoke legacy machine', 'Synthetic client', 'Synthetic location',
     'online', TRUE, 0, 0, 0, '', 'LOCAL-SMOKE-LEGACY-$runId', 'LOCAL-SMOKE-LEGACY-$runId', '$syntheticCompanyId', FALSE, FALSE);

INSERT INTO "MachineConnectionStates"
    ("Id", "MachineId", "CompanyId", "MonitoringEnabled", "IsOnline", "ConnectionId",
     "ConnectedAt", "LastSeenAt")
VALUES
    (gen_random_uuid(), '$legacyMachineId', '$syntheticCompanyId', FALSE, TRUE,
     '$legacyConnectionId', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
"@
    Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-tA", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "vendmachines", "-c", $telemetryFixtureSql
    ) | Out-Null

    $repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
    $simulatorProject = Join-Path $repositoryRoot "tools/VendingMachines.Simulator/VendingMachines.Simulator.csproj"
    $simulatorBuildOutput = & dotnet build $simulatorProject --configuration Release --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        $buildDetail = ($simulatorBuildOutput | Select-Object -Last 8 | Out-String).Trim()
        $buildDetail = [regex]::Replace($buildDetail, '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '[email-redacted]')
        throw "Falha ao compilar o simulador local para o smoke WebSocket: $buildDetail"
    }
    $simulatorAssembly = Join-Path $repositoryRoot "tools/VendingMachines.Simulator/bin/Release/net10.0/vend-machine.dll"
    if (-not (Test-Path -LiteralPath $simulatorAssembly -PathType Leaf)) {
        throw "O assembly compilado do simulador local não foi encontrado."
    }
    $connectionString = "Host=readiness-postgres;Port=5432;Database=vendmachines;Username=postgres;Password=$postgresPassword;SSL Mode=Require;Trust Server Certificate=true"
    $runtimeEnvironment = @(
        "ASPNETCORE_ENVIRONMENT=Production",
        "DOTNET_ENVIRONMENT=Production",
        "ConnectionStrings__DefaultConnection=$connectionString",
        "Encryption__Key=LocalReadinessEncryptionCurrentKey2026",
        "Encryption__LegacyKey=LocalReadinessEncryptionLegacyKey2026",
        "Encryption__WriteLegacyFormat=false",
        "Jwt__Key=LocalReadinessJwtSigningKeySynthetic2026-NotASecret",
        "MercadoPago__ClientId=LOCAL-READINESS-CLIENT-20261101",
        "MercadoPago__ClientSecret=local-readiness-client-secret-20261101",
        "MercadoPago__WebhookSecret=local-readiness-webhook-secret-20261101",
        "MercadoPago__WebhookUrl=https://api-smoke.invalid/hooks/mercadopago",
        "Cors__AllowedOrigins__0=https://web-smoke.invalid",
        "WebSockets__AllowedOrigins__0=https://web-smoke.invalid",
        "AccountEmail__Enabled=false",
        "Telemetry__RequireDeviceCredentials=false"
    )
    $apiArguments = @("run", "--detach", "--network", $networkName, "--name", $apiContainer)
    foreach ($setting in $runtimeEnvironment) {
        $apiArguments += @("--env", $setting)
    }
    $apiArguments += $Image
    Invoke-DockerChecked $apiArguments | Out-Null
    $apiCreated = $true

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $healthStatus = "starting"
    while ([DateTime]::UtcNow -lt $deadline) {
        $healthStatus = (Invoke-DockerChecked @(
            "inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}", $apiContainer
        ) | Out-String).Trim()
        if ($healthStatus -eq "healthy") {
            break
        }
        if ($healthStatus -in @("unhealthy", "exited", "dead")) {
            throw "Health check da imagem backend terminou como '$healthStatus'."
        }
        Start-Sleep -Seconds 1
    }
    if ($healthStatus -ne "healthy") {
        throw "Health check da imagem backend não ficou saudável em $StartupTimeoutSeconds segundos."
    }

    $legacyPresenceQuery = "SELECT CASE WHEN `"IsOnline`" AND `"ConnectionId`" = '$legacyConnectionId' AND `"OwnerNodeId`" IS NULL AND `"OwnerConnectionId`" IS NULL THEN 'preserved' ELSE 'changed' END FROM `"MachineConnectionStates`" WHERE `"MachineId`" = '$legacyMachineId';"
    $legacyPresenceAfterStartup = (Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines", "-c", $legacyPresenceQuery
    ) | Out-String).Trim()
    if ($legacyPresenceAfterStartup -ne "preserved") {
        throw "O startup Production alterou a presença online sintética pertencente ao binário legado."
    }

    $readinessFeeSetupSql = @'
INSERT INTO "SystemSettings" ("Id", "IsSingleton", "ApplicationFeePercent", "UpdatedAt")
VALUES (gen_random_uuid(), TRUE, 5.00, CURRENT_TIMESTAMP);
'@
    Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "vendmachines", "-c", $readinessFeeSetupSql
    ) | Out-Null

    $openAdmissionResponse = Get-ContainerHttpResponse "/health/deploy-ready"
    if ($openAdmissionResponse -notmatch '(?m)^HTTP/1\.[01] 503(?:\s|$)' -or
        $openAdmissionResponse -notmatch '"reason"\s*:\s*"legacy_owner_api_unavailable"') {
        throw "A admissão pode permanecer aberta, mas o readiness precisa bloquear a conexão legada sem relay privado saudável: $openAdmissionResponse"
    }

    $readinessSetupSql = @'
SELECT "NewSessionsPaused" FROM "SessionAdmissionGates" WHERE "Id" = 1;
'@
    $admissionPaused = (Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-tA", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "vendmachines", "-c", $readinessSetupSql
    ) | Out-String).Trim()
    if ($admissionPaused -ne "f") {
        throw "O smoke test deve manter a admissão de sessões aberta durante o preflight de continuidade."
    }

    Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "vendmachines",
        "-c", "UPDATE `"MachineConnectionStates`" SET `"IsOnline`" = FALSE, `"ConnectionId`" = NULL, `"DisconnectedAt`" = CURRENT_TIMESTAMP WHERE `"MachineId`" = '$legacyMachineId';"
    ) | Out-Null

    $readyDeployResponse = Get-ContainerHttpResponse "/health/deploy-ready"
    if ($readyDeployResponse -notmatch '(?m)^HTTP/1\.[01] 200(?:\s|$)' -or
        $readyDeployResponse -notmatch '"status"\s*:\s*"Ready"') {
        throw "O endpoint de deploy readiness não liberou a release após remover a presença legada mantendo a admissão aberta: $readyDeployResponse"
    }

    $routedActiveSessionSetupSql = @"
UPDATE "MachineConnectionStates"
SET "IsOnline" = TRUE,
    "ConnectionId" = 'routed-active-session-connection',
    "OwnerNodeId" = 'smoke-owner-node',
    "OwnerConnectionId" = 'routed-active-session-connection',
    "CommandRoutingVersion" = 1,
    "OwnerLeaseExpiresAt" = CURRENT_TIMESTAMP + INTERVAL '1 minute',
    "LastSeenAt" = CURRENT_TIMESTAMP
WHERE "MachineId" = '$legacyMachineId';
INSERT INTO "MachineSessions" ("Id", "MachineId", "CompanyId", "Source", "State", "StartedAt", "LastEventAt")
VALUES (gen_random_uuid(), '$legacyMachineId', '$syntheticCompanyId', 'manual', 'AwaitingSelection', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
"@
    Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "vendmachines", "-c", $routedActiveSessionSetupSql
    ) | Out-Null
    $routedActiveSessionResponse = Get-ContainerHttpResponse "/health/deploy-ready"
    if ($routedActiveSessionResponse -notmatch '(?m)^HTTP/1\.[01] 200(?:\s|$)' -or
        $routedActiveSessionResponse -notmatch '"status"\s*:\s*"Ready"') {
        throw "O readiness não permitiu a sessão ativa cuja máquina permaneceu roteável: $routedActiveSessionResponse"
    }

    Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "vendmachines",
        "-c", "UPDATE `"MachineConnectionStates`" SET `"IsOnline`" = FALSE, `"ConnectionId`" = NULL, `"OwnerNodeId`" = NULL, `"OwnerConnectionId`" = NULL, `"CommandRoutingVersion`" = NULL, `"OwnerLeaseExpiresAt`" = NULL, `"DisconnectedAt`" = CURRENT_TIMESTAMP WHERE `"MachineId`" = '$legacyMachineId';"
    ) | Out-Null
    $unroutedActiveSessionResponse = Get-ContainerHttpResponse "/health/deploy-ready"
    if ($unroutedActiveSessionResponse -notmatch '(?m)^HTTP/1\.[01] 503(?:\s|$)' -or
        $unroutedActiveSessionResponse -notmatch '"reason"\s*:\s*"active_session_without_routable_machine"') {
        throw "O readiness não bloqueou a sessão ativa depois que a rota da máquina foi perdida: $unroutedActiveSessionResponse"
    }
    Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "vendmachines",
        "-c", "DELETE FROM `"MachineSessions`" WHERE `"MachineId`" = '$legacyMachineId';"
    ) | Out-Null

    Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "postgres", "-c", "CREATE DATABASE vendmachines_pending;"
    ) | Out-Null
    $pendingConnectionString = $connectionString.Replace("Database=vendmachines", "Database=vendmachines_pending", [StringComparison]::Ordinal)
    $pendingRuntimeEnvironment = @($runtimeEnvironment | ForEach-Object {
        if ($_.StartsWith("ConnectionStrings__DefaultConnection=", [StringComparison]::Ordinal)) {
            "ConnectionStrings__DefaultConnection=$pendingConnectionString"
        }
        else {
            $_
        }
    })
    $pendingApiArguments = @("run", "--detach", "--network", $networkName, "--name", $pendingApiContainer)
    foreach ($setting in $pendingRuntimeEnvironment) {
        $pendingApiArguments += @("--env", $setting)
    }
    $pendingApiArguments += $Image
    Invoke-DockerChecked $pendingApiArguments | Out-Null

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $pendingApiStatus = "created"
    while ([DateTime]::UtcNow -lt $deadline) {
        $pendingApiStatus = (Invoke-DockerChecked @(
            "inspect", "--format", "{{.State.Status}}", $pendingApiContainer
        ) | Out-String).Trim()
        if ($pendingApiStatus -eq "exited") {
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if ($pendingApiStatus -ne "exited") {
        throw "A imagem backend não encerrou ao encontrar migrations pendentes no banco descartável."
    }
    $pendingApiExitCode = (Invoke-DockerChecked @(
        "inspect", "--format", "{{.State.ExitCode}}", $pendingApiContainer
    ) | Out-String).Trim()
    $pendingApiLogs = (& docker logs $pendingApiContainer 2>&1 | Out-String)
    if ([int]$pendingApiExitCode -eq 0 -or
        $pendingApiLogs -notmatch 'Há \d+ migração\(ões\) pendente\(s\)\. Revise e aplique o script de release antes de iniciar a API\.') {
        throw "A imagem Production não recusou o banco sem migrations de forma fail-closed."
    }
    $pendingHistoryState = (Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines_pending",
        "-c", 'SELECT to_regclass(''"__EFMigrationsHistory"'') IS NULL;'
    ) | Out-String).Trim()
    if ($pendingHistoryState -ne "t") {
        throw "A imagem backend alterou o histórico de migrations do banco pendente durante o startup."
    }

    $simulatorOutputDirectory = Split-Path -Parent $simulatorAssembly
    Invoke-DockerChecked @(
        "create", "--network", $networkName, "--name", $simulatorContainer, "--entrypoint", "dotnet", $Image,
        "/tmp/vend-machine.dll",
        "--serial", $simulatorSerial,
        "--url", "ws://${apiContainer}:8080/telemetria",
        "--heartbeat", "5",
        "--headless"
    ) | Out-Null
    $simulatorCreated = $true
    Invoke-DockerChecked @("cp", "$simulatorOutputDirectory/.", "${simulatorContainer}:/tmp/") | Out-Null
    Invoke-DockerChecked @("start", $simulatorContainer) | Out-Null

    $ownerQuery = "SELECT COALESCE(`"OwnerNodeId`", '') || '|' || COALESCE(`"OwnerConnectionId`", '') FROM `"MachineConnectionStates`" WHERE `"MachineId`" = '$simulatorMachineId' AND `"IsOnline`" = TRUE AND `"CommandRoutingVersion`" = 2;"
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $initialTelemetryOwner = ""
    while ([DateTime]::UtcNow -lt $deadline) {
        $simulatorStatus = (Invoke-DockerChecked @("inspect", "--format", "{{.State.Status}}", $simulatorContainer) | Out-String).Trim()
        if ($simulatorStatus -ne "running") {
            $simulatorFailure = (& docker logs $simulatorContainer 2>&1 | Select-Object -Last 20 | Out-String).Trim()
            throw "O simulador WebSocket encerrou antes de conectar: $simulatorFailure"
        }
        $initialTelemetryOwner = (Invoke-DockerChecked @(
            "exec", $postgresContainer, "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines", "-c", $ownerQuery
        ) | Out-String).Trim()
        if (-not [string]::IsNullOrWhiteSpace($initialTelemetryOwner)) {
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if ([string]::IsNullOrWhiteSpace($initialTelemetryOwner)) {
        $simulatorOutput = (& docker logs $simulatorContainer 2>&1 | Select-Object -Last 30 | Out-String).Trim()
        $apiLogs = (& docker logs $apiContainer 2>&1 | Select-Object -Last 20 | Out-String).Trim()
        throw "O simulador local não estabeleceu presença WebSocket no container Production. simulator=$simulatorOutput api=$apiLogs"
    }

    $stopOutput = & docker stop --time 30 $apiContainer 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível encerrar graciosamente o primeiro container para o ensaio de reconexão."
    }
    $removeOutput = & docker rm $apiContainer 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível remover o primeiro container após o shutdown gracioso."
    }
    Invoke-DockerChecked $apiArguments | Out-Null

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $healthStatus = "starting"
    while ([DateTime]::UtcNow -lt $deadline) {
        $healthStatus = (Invoke-DockerChecked @(
            "inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}", $apiContainer
        ) | Out-String).Trim()
        if ($healthStatus -eq "healthy") {
            break
        }
        if ($healthStatus -in @("unhealthy", "exited", "dead")) {
            throw "Health check da imagem backend após substituição terminou como '$healthStatus'."
        }
        Start-Sleep -Seconds 1
    }
    if ($healthStatus -ne "healthy") {
        throw "Health check da imagem backend após substituição não ficou saudável em $StartupTimeoutSeconds segundos."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $reconnectedTelemetryOwner = ""
    while ([DateTime]::UtcNow -lt $deadline) {
        $simulatorStatus = (Invoke-DockerChecked @("inspect", "--format", "{{.State.Status}}", $simulatorContainer) | Out-String).Trim()
        if ($simulatorStatus -ne "running") {
            $simulatorFailure = (& docker logs $simulatorContainer 2>&1 | Select-Object -Last 20 | Out-String).Trim()
            throw "O simulador WebSocket encerrou durante a substituição: $simulatorFailure"
        }
        $reconnectedTelemetryOwner = (Invoke-DockerChecked @(
            "exec", $postgresContainer, "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines", "-c", $ownerQuery
        ) | Out-String).Trim()
        if (-not [string]::IsNullOrWhiteSpace($reconnectedTelemetryOwner) -and
            $reconnectedTelemetryOwner -ne $initialTelemetryOwner) {
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if ([string]::IsNullOrWhiteSpace($reconnectedTelemetryOwner) -or
        $reconnectedTelemetryOwner -eq $initialTelemetryOwner) {
        throw "O simulador não restabeleceu a conexão WebSocket com um novo proprietário após a substituição do container."
    }

    $telemetryStateCount = (Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines",
        "-c", "SELECT COUNT(*) FROM `"MachineConnectionStates`" WHERE `"MachineId`" = '$simulatorMachineId' AND `"IsOnline`" = TRUE;"
    ) | Out-String).Trim()
    if ([int]$telemetryStateCount -ne 1) {
        throw "A reconexão criou presença duplicada ou não deixou uma única presença online no banco sintético."
    }
    $legacyDrainedQuery = "SELECT CASE WHEN NOT `"IsOnline`" AND `"ConnectionId`" IS NULL AND `"OwnerNodeId`" IS NULL AND `"OwnerConnectionId`" IS NULL THEN 'drained' ELSE 'changed' END FROM `"MachineConnectionStates`" WHERE `"MachineId`" = '$legacyMachineId';"
    $legacyPresenceAfterReplacement = (Invoke-DockerChecked @(
        "exec", $postgresContainer, "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines", "-c", $legacyDrainedQuery
    ) | Out-String).Trim()
    if ($legacyPresenceAfterReplacement -ne "drained") {
        throw "A substituição da imagem Production reativou ou atribuiu owner à presença legada sintética já drenada."
    }
    $simulatorLogs = (& docker logs $simulatorContainer 2>&1 | Out-String)
    if ([regex]::Matches($simulatorLogs, "Máquina conectada e identificada\.").Count -lt 2) {
        throw "Os logs locais do simulador não confirmaram dois handshakes MDB concluídos."
    }

    Write-Output "Imagem Production saudável com PostgreSQL 17/TLS e $migrationCount migrations; readiness bloqueou presença legada sem owner, liberou após limpeza sintética, migrations pendentes foram recusadas sem escrita e um simulador MDB reconectou após a substituição do container. Nenhuma porta foi publicada e a rede Docker era interna."
}
finally {
    if ($simulatorCreated) {
        Remove-DockerContainerIfPresent $simulatorContainer
    }
    Remove-DockerContainerIfPresent $pendingApiContainer
    Remove-DockerContainerIfPresent $apiContainer
    Remove-DockerContainerIfPresent $postgresContainer
    if ($networkCreated) {
        & docker network rm $networkName *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Não foi possível remover a rede interna temporária: $networkName."
        }
    }
    if ($certificate) { $certificate.Dispose() }
    if ($rsa) { $rsa.Dispose() }
    if (Test-Path -LiteralPath $overridePath) { Remove-Item -LiteralPath $overridePath -Force }
    if (Test-Path -LiteralPath $tlsKeyPath) { Remove-Item -LiteralPath $tlsKeyPath -Force }
    if (Test-Path -LiteralPath $tlsCertificatePath) { Remove-Item -LiteralPath $tlsCertificatePath -Force }
    if (Test-Path -LiteralPath $tlsDirectory) { Remove-Item -LiteralPath $tlsDirectory -Force }
}
