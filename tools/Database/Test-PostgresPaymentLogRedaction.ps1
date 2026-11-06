[CmdletBinding()]
param(
    [string]$PostgresImage = "postgres:17-alpine"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$containerName = "vendmachines-payment-log-redaction-test-$([Guid]::NewGuid().ToString('N'))"
$containerStarted = $false
$sqlFile = Join-Path $PSScriptRoot "Redact-PaymentTelemetrySensitiveMessages.sql"

function Invoke-DockerChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | Select-Object -Last 3 | Out-String).Trim()
        throw "Falha no ensaio isolado de redação dos logs de pagamento (código $LASTEXITCODE): $detail"
    }
    return $output
}

try {
    Invoke-DockerChecked @(
        "run", "--rm", "--detach", "--network", "none", "--name", $containerName,
        "--env", "POSTGRES_PASSWORD=synthetic-payment-log-redaction-test-only", $PostgresImage
    ) | Out-Null
    $containerStarted = $true

    $ready = $false
    $consecutiveReadyChecks = 0
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        & docker exec $containerName pg_isready -U postgres -d postgres *> $null
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
    if (-not $ready) { throw "O PostgreSQL sintético não ficou pronto dentro de 60 segundos." }

    $syntheticHeader = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"alg":"HS256"}')).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $syntheticPayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"sub":"synthetic"}')).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $syntheticSignature = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('synthetic-signature')).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $syntheticJwt = "$syntheticHeader.$syntheticPayload.$syntheticSignature"

    $fixtureSql = @'
CREATE TABLE "TransactionTelemetryLogs" ("Id" uuid PRIMARY KEY, "Message" text NULL);
INSERT INTO "TransactionTelemetryLogs" ("Id", "Message")
SELECT lpad(i::text, 32, '0')::uuid,
       format('Gateway diagnostic: access_token=synthetic-provider-token-%s contact=private-%s@example.test', i, i)
FROM generate_series(1, 205) AS i;
INSERT INTO "TransactionTelemetryLogs" VALUES
('00000000-0000-0000-0000-000000000206', 'Solicitada cobrança Pix na máquina Máquina A.'),
('00000000-0000-0000-0000-000000000207', 'Authorization: Bearer synthetic-bearer-value'),
('00000000-0000-0000-0000-000000000208', 'Resposta: APP_USR-123456789012-synthetic-provider-value'),
('00000000-0000-0000-0000-000000000209', 'Erro retornado pelo Mercado Pago: resposta sintética privada'),
('00000000-0000-0000-0000-000000000210', 'Gateway diagnostic: jwt=__SYNTHETIC_JWT__'),
('00000000-0000-0000-0000-000000000211', NULL),
('00000000-0000-0000-0000-000000000212', 'Gateway response JSON: {"access_token":"json-provider-token","refresh_token":"json-refresh-token","contact":"json-user@example.test"}');
'@
    $fixtureSql = $fixtureSql.Replace('__SYNTHETIC_JWT__', $syntheticJwt)
    Invoke-DockerChecked @(
        "exec", "--interactive", $containerName, "psql", "-X", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "postgres", "-c", $fixtureSql
    ) | Out-Null
    Invoke-DockerChecked @("cp", $sqlFile, "${containerName}:/tmp/redact-payment-logs.sql") | Out-Null

    $dryRunOutput = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-A", "-t", "-F", "|", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "postgres", "-f", "/tmp/redact-payment-logs.sql"
    )
    $dryRunText = $dryRunOutput -join "`n"
    if ($dryRunText -notmatch "(?m)^211\|210$") {
        throw "As contagens do preflight não correspondem ao fixture sintético."
    }
    if ($dryRunText -match "synthetic-provider-token|synthetic-bearer-value|private-[0-9]+@example\.test|json-provider-token|json-refresh-token|json-user@example\.test") {
        throw "O preflight expôs um valor sintético que deveria permanecer privado."
    }

    $unchangedRows = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-A", "-t", "-U", "postgres", "-d", "postgres",
        "-c", 'SELECT count(*) FROM "TransactionTelemetryLogs" WHERE "Message" LIKE ''%synthetic-provider-token%'';'
    )
    if (($unchangedRows -join "").Trim() -ne "205") { throw "O preflight alterou linhas antes de apply=true." }

    $unchangedJsonRows = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-A", "-t", "-U", "postgres", "-d", "postgres",
        "-c", 'SELECT count(*) FROM "TransactionTelemetryLogs" WHERE "Message" LIKE ''%json-provider-token%'';'
    )
    if (($unchangedJsonRows -join "").Trim() -ne "1") { throw "O preflight alterou o JSON antes de apply=true." }

    $applyOutput = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-A", "-t", "-F", "|", "-v", "ON_ERROR_STOP=1",
        "-v", "apply=true", "-U", "postgres", "-d", "postgres", "-f", "/tmp/redact-payment-logs.sql"
    )
    $applyText = $applyOutput -join "`n"
    if ($applyText -notmatch "(?m)^211\|210$" -or $applyText -notmatch "(?m)^0$" -or
        $applyText -match "synthetic-provider-token|synthetic-bearer-value|private-[0-9]+@example\.test|json-provider-token|json-refresh-token|json-user@example\.test") {
        throw "A execução controlada ou o preflight final não produziu as contagens esperadas."
    }

    $finalCounts = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-A", "-t", "-F", "|", "-U", "postgres", "-d", "postgres",
        "-c", 'SELECT count(*) FILTER (WHERE "Message" LIKE ''%synthetic-provider-token%'' OR "Message" LIKE ''%synthetic-signature%'' OR "Message" LIKE ''%json-provider-token%'' OR "Message" LIKE ''%json-refresh-token%''), count(*) FILTER (WHERE "Message" = ''Solicitada cobrança Pix na máquina Máquina A.''), count(*) FILTER (WHERE "Message" IS NULL), count(*) FILTER (WHERE "Message" LIKE ''%[REDACTED]%'' OR "Message" LIKE ''%[REDACTED_EMAIL]%'' ) FROM "TransactionTelemetryLogs";'
    )
    if (($finalCounts -join "").Trim() -ne "0|1|1|209") {
        throw "Os logs finais do fixture divergem: $($finalCounts -join ' ')."
    }

    $validJsonRows = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-A", "-t", "-U", "postgres", "-d", "postgres",
        "-c", 'SELECT count(*) FROM "TransactionTelemetryLogs" WHERE "Id" = ''00000000-0000-0000-0000-000000000212'' AND substring("Message" FROM position(''{'' IN "Message"))::jsonb = ''{"access_token":"[REDACTED]","refresh_token":"[REDACTED]","contact":"[REDACTED_EMAIL]"}''::jsonb;'
    )
    if (($validJsonRows -join "").Trim() -ne "1") {
        throw "O objeto JSON não permaneceu válido com os campos sensíveis redigidos."
    }

    Write-Host "Preflight count-only, dry-run sem alterações e redação em lotes passaram no PostgreSQL sintético."
}
finally {
    if ($containerStarted) {
        & docker stop $containerName *> $null
        if ($LASTEXITCODE -ne 0) { & docker rm --force $containerName *> $null }
    }
}
