[CmdletBinding()]
param(
    [string]$PostgresImage = "postgres:17-alpine"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$containerName = "vendmachines-audit-redaction-test-$([Guid]::NewGuid().ToString('N'))"
$containerStarted = $false
$sqlFile = Join-Path $PSScriptRoot "Redact-AuditLogSensitiveMetadata.sql"

function Invoke-DockerChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | Select-Object -Last 3 | Out-String).Trim()
        throw "Falha no ensaio isolado de redação de auditoria (código $LASTEXITCODE): $detail"
    }
    return $output
}

try {
    Invoke-DockerChecked @(
        "run", "--rm", "--detach", "--network", "none", "--name", $containerName,
        "--env", "POSTGRES_PASSWORD=synthetic-audit-redaction-test-only", $PostgresImage
    ) | Out-Null
    $containerStarted = $true

    $ready = $false
    $consecutiveReadyChecks = 0
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        & docker exec $containerName pg_isready -h 127.0.0.1 -U postgres -d postgres *> $null
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

    $fixtureSql = @'
CREATE TABLE "AuditLogs" ("Id" uuid PRIMARY KEY, "MetadataJson" text NULL);
INSERT INTO "AuditLogs" ("Id", "MetadataJson")
SELECT lpad(i::text, 32, '0')::uuid,
       format('{"accessToken":"synthetic-private-token-%s","nested":{"email":"private@example.test"},"reason":"safe"}', i)
FROM generate_series(1, 205) AS i;
INSERT INTO "AuditLogs" VALUES
('00000000-0000-0000-0000-000000000206', '{"reason":"safe"}'),
('00000000-0000-0000-0000-000000000207', 'not-json'),
('00000000-0000-0000-0000-000000000208', '"scalar-private-value"'),
('00000000-0000-0000-0000-000000000209', NULL);
'@
    Invoke-DockerChecked @(
        "exec", "--interactive", $containerName, "psql", "-X", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "postgres", "-c", $fixtureSql
    ) | Out-Null
    Invoke-DockerChecked @("cp", $sqlFile, "${containerName}:/tmp/redact.sql") | Out-Null

    $dryRunOutput = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-A", "-t", "-F", "|", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "postgres", "-f", "/tmp/redact.sql"
    )
    $dryRunText = $dryRunOutput -join "`n"
    if ($dryRunText -notmatch "(?m)^208\|1\|1\|205\|207$") {
        throw "As contagens do preflight não correspondem ao fixture sintético."
    }
    if ($dryRunText -match "synthetic-private-token|scalar-private-value|private@example\.test") {
        throw "O preflight expôs um valor sintético que deveria permanecer privado."
    }

    $unchangedRows = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-A", "-t", "-U", "postgres", "-d", "postgres",
        "-c", 'SELECT count(*) FROM "AuditLogs" WHERE "MetadataJson" LIKE ''%synthetic-private-token%'';'
    )
    if (($unchangedRows -join "").Trim() -ne "205") {
        throw "O preflight alterou os metadados antes de apply=true."
    }

    $applyOutput = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-A", "-t", "-F", "|", "-v", "ON_ERROR_STOP=1",
        "-v", "apply=true", "-U", "postgres", "-d", "postgres", "-f", "/tmp/redact.sql"
    )
    $applyText = $applyOutput -join "`n"
    if ($applyText -notmatch "(?m)^208\|1\|1\|205\|207$" -or
        $applyText -notmatch "(?m)^0$" -or
        $applyText -match "synthetic-private-token|scalar-private-value|private@example\.test") {
        throw "A execução controlada ou o preflight final não produziu as contagens esperadas."
    }

    $finalCounts = Invoke-DockerChecked @(
        "exec", $containerName, "psql", "-X", "-A", "-t", "-F", "|", "-U", "postgres", "-d", "postgres",
        "-c", 'SELECT count(*) FILTER (WHERE "MetadataJson" LIKE ''%synthetic-private-token%''), count(*) FILTER (WHERE "MetadataJson" = ''{"reason":"safe"}''), count(*) FILTER (WHERE "MetadataJson" IS NULL), count(*) FILTER (WHERE "MetadataJson" LIKE ''%[REDACTED]%'' ) FROM "AuditLogs";'
    )
    if (($finalCounts -join "").Trim() -ne "0|1|3|205") {
        throw "Os metadados finais do fixture divergem: $($finalCounts -join ' ')."
    }

    Write-Host "Preflight count-only, dry-run sem alterações e redação em lotes passaram no PostgreSQL sintético."
}
finally {
    if ($containerStarted) {
        & docker stop $containerName *> $null
        if ($LASTEXITCODE -ne 0) {
            & docker rm --force $containerName *> $null
        }
    }
}
