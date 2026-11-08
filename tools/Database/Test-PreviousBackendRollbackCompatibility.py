#!/usr/bin/env python3
"""Rehearse a previous API image against the candidate schema using Docker only."""

from __future__ import annotations

import argparse
import base64
from concurrent.futures import ThreadPoolExecutor
import hashlib
import hmac
import json
import re
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path


SEGMENTS = (
    "postgresql-migrations-before-session-index.sql",
    "postgresql-migration-session-index.sql",
    "postgresql-migrations-between-online-indexes.sql",
    "postgresql-migration-payment-reconciliation-index.sql",
    "postgresql-migrations-after-payment-index.sql",
    "postgresql-migration-inventory-mapping-index.sql",
    "postgresql-migrations-after-inventory-index.sql",
)
BOOTSTRAP_SEGMENT = "postgresql-migrations-after-payment-index.sql"
BOOTSTRAP_MIGRATION = "20260927163720_BootstrapPlatformAdmin"
LOCK_TIMEOUT_PREFIX = "SET lock_timeout = '5s';\n"


class RehearsalError(RuntimeError):
    pass


def run(*args: str, cwd: Path | None = None, check: bool = True) -> str:
    completed = subprocess.run(
        args,
        cwd=cwd,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False,
    )
    output = completed.stdout.strip()
    if check and completed.returncode != 0:
        tail = "\n".join(output.splitlines()[-12:])
        raise RehearsalError(
            f"{Path(args[0]).name} falhou com código {completed.returncode}:\n{tail}"
        )
    return output


def query(container: str, database: str, sql: str) -> str:
    return run(
        "docker", "exec", container, "psql", "-X", "-tA", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", database, "-c", sql,
    ).strip()


def create_test_jwt(signing_key: str, user_id: str, company_id: str) -> str:
    def encode(value: bytes) -> str:
        return base64.urlsafe_b64encode(value).decode("ascii").rstrip("=")

    now = int(time.time())
    header = encode(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
    payload = encode(json.dumps({
        "iss": "VendingMachinesApi",
        "aud": "VendingMachinesApp",
        "sub": user_id,
        "nameid": user_id,
        "company_id": company_id,
        "role": "PlatformAdmin",
        "credential_version": 1,
        "iat": now,
        "exp": now + 300,
    }, separators=(",", ":")).encode())
    unsigned = f"{header}.{payload}"
    signature = hmac.new(signing_key.encode(), unsigned.encode(), hashlib.sha256).digest()
    return f"{unsigned}.{encode(signature)}"


def attempt(*args: str) -> tuple[int, str]:
    completed = subprocess.run(args, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
    return completed.returncode, completed.stdout.strip()


def prepare_bootstrap_segment(repo: Path, artifacts: Path, temp: Path) -> Path:
    segment = (artifacts / BOOTSTRAP_SEGMENT).read_text(encoding="utf-8-sig")
    occurrences = [match.start() for match in re.finditer(BOOTSTRAP_MIGRATION, segment)]
    if len(occurrences) != 3:
        raise RehearsalError("Não foi possível identificar a transação da migration bootstrap.")
    transaction_start = segment.rfind("START TRANSACTION;", 0, occurrences[0])

    source = (repo / "apps/backend/VendingMachines.Api/Migrations/20260927163720_BootstrapPlatformAdmin.cs").read_text(
        encoding="utf-8"
    )
    reserved_emails = set(re.findall(r'"NormalizedEmail"\s*=\s*\'([^\']+)\'', source))
    if transaction_start < 0 or len(reserved_emails) != 1:
        raise RehearsalError("Não foi possível preparar a conta sintética de bootstrap.")

    email = next(iter(reserved_emails)).replace("'", "''")
    account = (
        'INSERT INTO "Users" '
        '("Id","Name","Email","NormalizedEmail","PasswordHash","Role","IsActive",'
        '"CredentialVersion","CompanyId","CreatedAt","Cpf","NormalizedCpf")\n'
        f"VALUES (gen_random_uuid(),'Synthetic rollback account','{email}','{email}',"
        "'invalid-synthetic-hash','Admin',TRUE,0,NULL,CURRENT_TIMESTAMP,'','');\n\n"
    )
    prepared = temp / BOOTSTRAP_SEGMENT
    prepared.write_text(segment[:transaction_start] + account + segment[transaction_start:], encoding="utf-8")
    return prepared


def apply_candidate_schema(repo: Path, artifacts: Path, postgres: str, temp: Path) -> str:
    for name in SEGMENTS:
        path = artifacts / name
        if not path.is_file():
            raise RehearsalError(f"Segmento SQL ausente: {name}")
        if not path.read_text(encoding="utf-8-sig").startswith(LOCK_TIMEOUT_PREFIX):
            raise RehearsalError(f"Segmento sem lock_timeout=5s: {name}")

    override = prepare_bootstrap_segment(repo, artifacts, temp)
    for name in SEGMENTS:
        source = override if name == BOOTSTRAP_SEGMENT else artifacts / name
        run("docker", "cp", str(source), f"{postgres}:/tmp/{name}")
        run(
            "docker", "exec", postgres, "psql", "-X", "-v", "ON_ERROR_STOP=1",
            "-U", "postgres", "-d", "vendmachines", "-f", f"/tmp/{name}",
        )
    return query(postgres, "vendmachines", 'SELECT COUNT(*) FROM "__EFMigrationsHistory";')


def configure_postgres_tls(postgres: str, temp: Path) -> None:
    cert_dir = temp / "postgres-tls"
    cert_dir.mkdir()
    project = cert_dir / "CertGen.csproj"
    source = cert_dir / "Program.cs"
    project.write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
        '<Nullable>enable</Nullable></PropertyGroup></Project>',
        encoding="utf-8",
    )
    source.write_text(
        """using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
var directory = args[0];
using var rsa = RSA.Create(2048);
var request = new CertificateRequest("CN=rollback-postgres", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
File.WriteAllText(Path.Combine(directory, "server.crt"), certificate.ExportCertificatePem());
File.WriteAllText(Path.Combine(directory, "server.key"), rsa.ExportPkcs8PrivateKeyPem());
""",
        encoding="utf-8",
    )
    run("dotnet", "run", "--project", str(project), "--configuration", "Release", "--", str(cert_dir), cwd=cert_dir)
    run("docker", "cp", str(cert_dir / "server.key"), f"{postgres}:/tmp/server.key")
    run("docker", "cp", str(cert_dir / "server.crt"), f"{postgres}:/tmp/server.crt")
    run(
        "docker", "exec", "--user", "root", postgres, "sh", "-c",
        "chown postgres:postgres /tmp/server.key /tmp/server.crt && "
        "chmod 600 /tmp/server.key && chmod 644 /tmp/server.crt && "
        "mv /tmp/server.key \"$PGDATA/server.key\" && mv /tmp/server.crt \"$PGDATA/server.crt\"",
    )
    run("docker", "exec", postgres, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "postgres", "-c", "ALTER SYSTEM SET ssl = 'on';")
    run("docker", "exec", postgres, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "postgres", "-c", "SELECT pg_reload_conf();")
    if query(postgres, "postgres", "SHOW ssl;") != "on":
        raise RehearsalError("TLS não foi habilitado no PostgreSQL descartável.")


def add_startup_trace(
    worktree: Path,
    skip_database_initialization: bool = False,
    disabled_hosted_service: str | None = None,
    yield_mdb_worker_startup: bool = False,
) -> None:
    targets = {
        worktree / "apps/backend/VendingMachines.Api/Program.cs": (
            (
                "var builder = WebApplication.CreateBuilder(args);",
                "Console.Error.WriteLine(\"TRACE top-level before CreateBuilder\");\n"
                "var builder = WebApplication.CreateBuilder(args);\n"
                "Console.Error.WriteLine(\"TRACE after CreateBuilder\");",
            ),
            (
                "var app = builder.Build();",
                "Console.Error.WriteLine(\"TRACE before builder.Build\");\n"
                "var app = builder.Build();\n"
                "Console.Error.WriteLine(\"TRACE after builder.Build\");",
            ),
            (
                "using (var scope = app.Services.CreateScope())\n{",
                "Console.Error.WriteLine(\"TRACE before startup scope\");\n"
                "using (var scope = app.Services.CreateScope())\n{\n"
                "    Console.Error.WriteLine(\"TRACE after startup scope\");",
            ),
            (
                "var logger = services.GetRequiredService<ILogger<Program>>();",
                "Console.Error.WriteLine(\"TRACE before startup logger\");\n"
                "var logger = services.GetRequiredService<ILogger<Program>>();\n"
                "Console.Error.WriteLine(\"TRACE after startup logger\");",
            ),
            (
                "var context = services.GetRequiredService<AppDbContext>();",
                "Console.Error.WriteLine(\"TRACE before DbContextOptions resolution\");\n"
                "var contextOptions = services.GetRequiredService<DbContextOptions<AppDbContext>>();\n"
                "Console.Error.WriteLine(\"TRACE after DbContextOptions resolution\");\n"
                "Console.Error.WriteLine(\"TRACE before direct AppDbContext construction\");\n"
                "using var context = new AppDbContext(contextOptions);\n"
                "Console.Error.WriteLine(\"TRACE after direct AppDbContext construction\");",
            ),
            (
                "        DbInitializer.Initialize(context, app.Environment.IsDevelopment());",
                "        Console.Error.WriteLine(\"TRACE before DbInitializer.Initialize\");\n"
                "        DbInitializer.Initialize(context, app.Environment.IsDevelopment());\n"
                "        Console.Error.WriteLine(\"TRACE after DbInitializer.Initialize\");",
            ),
            (
                "        context.MachineConnectionStates.ExecuteUpdate(setters => setters",
                "        Console.Error.WriteLine(\"TRACE before startup presence reset\");\n"
                "        context.MachineConnectionStates.ExecuteUpdate(setters => setters",
            ),
            (
                "            .SetProperty(x => x.DisconnectedAt, DateTime.UtcNow));",
                "            .SetProperty(x => x.DisconnectedAt, DateTime.UtcNow));\n"
                "        Console.Error.WriteLine(\"TRACE after startup presence reset\");",
            ),
            (
                "app.Run();",
                "Console.Error.WriteLine(\"TRACE before app.Run\");\napp.Run();",
            ),
        ),
        worktree / "apps/backend/VendingMachines.Api/Data/DbInitializer.cs": (
            (
                "            context.Database.Migrate();",
                "            Console.Error.WriteLine(\"TRACE before Database.Migrate\");\n"
                "            context.Database.Migrate();\n"
                "            Console.Error.WriteLine(\"TRACE after Database.Migrate\");",
            ),
            (
                "        CleanUpEmptyMachineIds(context);",
                "        Console.Error.WriteLine(\"TRACE before CleanUpEmptyMachineIds\");\n"
                "        CleanUpEmptyMachineIds(context);\n"
                "        Console.Error.WriteLine(\"TRACE after CleanUpEmptyMachineIds\");",
            ),
        ),
    }
    for path, replacements in targets.items():
        source = path.read_text(encoding="utf-8")
        for original, replacement in replacements:
            if source.count(original) != 1:
                raise RehearsalError(f"Ponto de trace ambíguo ou ausente em {path.name}: {original.strip()}")
            source = source.replace(original, replacement, 1)
        if skip_database_initialization and path.name == "Program.cs":
            source, init_count = re.subn(
                r'(?m)^\s*DbInitializer\.Initialize\(context, app\.Environment\.IsDevelopment\(\)\);$',
                lambda _: f'        File.AppendAllText("/tmp/startup-trace.txt", {json.dumps("DbInitializer.Initialize skipped" + chr(10))});',
                source,
            )
            source, reset_count = re.subn(
                r'(?ms)^\s*context\.MachineConnectionStates\.ExecuteUpdate\(setters => setters\s*'
                r'\.SetProperty\(x => x\.IsOnline, false\)\s*'
                r'\.SetProperty\(x => x\.ConnectionId, \(string\?\)null\)\s*'
                r'\.SetProperty\(x => x\.DisconnectedAt, DateTime\.UtcNow\)\);',
                lambda _: f'        File.AppendAllText("/tmp/startup-trace.txt", {json.dumps("startup presence reset skipped" + chr(10))});',
                source,
            )
            if init_count != 1 or reset_count != 1:
                raise RehearsalError("Não foi possível isolar DbInitializer e reset de presença no diagnóstico.")
        if disabled_hosted_service and path.name == "Program.cs":
            registrations = {
                "TelemetryConnectionManager": "builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryConnectionManager>());",
                "TelemetryDeadlineWorker": "builder.Services.AddHostedService<TelemetryDeadlineWorker>();",
                "MdbStatusPollingWorker": "builder.Services.AddHostedService<MdbStatusPollingWorker>();",
            }
            registration = registrations[disabled_hosted_service]
            if source.count(registration) != 1:
                raise RehearsalError(f"Registro do worker {disabled_hosted_service} não é único.")
            source = source.replace(
                registration,
                f'File.AppendAllText("/tmp/startup-trace.txt", {json.dumps(disabled_hosted_service + " disabled" + chr(10))});',
                1,
            )
        path.write_text(source, encoding="utf-8")
    program = worktree / "apps/backend/VendingMachines.Api/Program.cs"
    source = program.read_text(encoding="utf-8")
    milestones = (
        "builder.Services.AddControllers();",
        "builder.Services.AddHttpClient(\"MercadoPago\");",
        "builder.Services.AddOptions<AuthOptions>()",
        "builder.Services.AddDbContext<AppDbContext>(options =>",
        "builder.Services.AddScoped<IDataService, DatabaseDataService>();",
        "builder.Services.AddAuthentication(options =>",
        "builder.Services.AddSwaggerGen(c =>",
        "builder.Services.AddCors(options =>",
        "builder.Services.AddSingleton<WebSocketTicketService>();",
        "var app = builder.Build();",
    )
    for index, marker in enumerate(milestones, start=1):
        if source.count(marker) != 1:
            raise RehearsalError(f"Ponto de trace ambíguo ou ausente em Program.cs: {marker}")
        source = source.replace(marker, f'Console.Error.WriteLine("TRACE registration {index}");\n{marker}', 1)
    source = re.sub(
        r'Console\.Error\.WriteLine\("TRACE ([^"]+)"\);',
        lambda match: (
            f'Console.Error.WriteLine("TRACE {match.group(1)}");\n'
            f'File.AppendAllText("/tmp/startup-trace.txt", {json.dumps(match.group(1) + chr(10))});'
        ),
        source,
    )
    if source.count("app.Run();") != 1:
        raise RehearsalError("Ponto de trace ApplicationStarted ausente ou ambíguo.")
    source = source.replace(
        "app.Run();",
        'app.Lifetime.ApplicationStarted.Register(() => { Console.Error.WriteLine("TRACE ApplicationStarted"); File.AppendAllText("/tmp/startup-trace.txt", "ApplicationStarted\\n"); });\napp.Run();',
        1,
    )
    program.write_text(source, encoding="utf-8")
    initializer = worktree / "apps/backend/VendingMachines.Api/Data/DbInitializer.cs"
    source = initializer.read_text(encoding="utf-8")
    source = re.sub(
        r'Console\.Error\.WriteLine\("TRACE ([^"]+)"\);',
        lambda match: (
            f'Console.Error.WriteLine("TRACE {match.group(1)}");\n'
            f'File.AppendAllText("/tmp/startup-trace.txt", {json.dumps(match.group(1) + chr(10))});'
        ),
        source,
    )
    initializer.write_text(source, encoding="utf-8")
    service_targets = {
        "apps/backend/VendingMachines.Api/Services/TelemetryConnectionManager.cs": (
            (
                "        var workers = Enumerable.Range(0, 4).Select(_ => ProcessInboundAsync(stoppingToken)).ToArray();",
                '        File.AppendAllText("/tmp/startup-trace.txt", "TelemetryConnectionManager ExecuteAsync begin\\n");\n'
                "        var workers = Enumerable.Range(0, 4).Select(_ => ProcessInboundAsync(stoppingToken)).ToArray();",
            ),
            (
                "        var heartbeat = MonitorHeartbeatAsync(stoppingToken);\n        await Task.WhenAll(workers.Append(heartbeat));",
                '        var heartbeat = MonitorHeartbeatAsync(stoppingToken);\n'
                '        File.AppendAllText("/tmp/startup-trace.txt", "TelemetryConnectionManager workers scheduled\\n");\n'
                "        await Task.WhenAll(workers.Append(heartbeat));",
            ),
        ),
        "apps/backend/VendingMachines.Api/Services/TelemetryDeadlineWorker.cs": (
            (
                "        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));",
                '        File.AppendAllText("/tmp/startup-trace.txt", "TelemetryDeadlineWorker ExecuteAsync begin\\n");\n'
                "        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));",
            ),
        ),
        "apps/backend/VendingMachines.Api/Services/MdbStatusServices.cs": (
            (
                "    protected override async Task ExecuteAsync(CancellationToken stoppingToken)\n    {\n        while (!stoppingToken.IsCancellationRequested)",
                '    protected override async Task ExecuteAsync(CancellationToken stoppingToken)\n    {\n'
                '        File.AppendAllText("/tmp/startup-trace.txt", "MdbStatusPollingWorker ExecuteAsync begin\\n");\n'
                "        while (!stoppingToken.IsCancellationRequested)",
            ),
            (
                "                var machines = await GetDueMachinesAsync(stoppingToken);",
                '                var machines = await GetDueMachinesAsync(stoppingToken);\n'
                '                File.AppendAllText("/tmp/startup-trace.txt", "MdbStatusPollingWorker first query complete\\n");',
            ),
        ),
    }
    for relative_path, replacements in service_targets.items():
        path = worktree / relative_path
        source = path.read_text(encoding="utf-8")
        for original, replacement in replacements:
            if source.count(original) != 1:
                raise RehearsalError(f"Ponto de trace ausente ou ambíguo em {relative_path}: {original.strip()}")
            source = source.replace(original, replacement, 1)
        path.write_text(source, encoding="utf-8")
    if yield_mdb_worker_startup:
        path = worktree / "apps/backend/VendingMachines.Api/Services/MdbStatusServices.cs"
        source = path.read_text(encoding="utf-8")
        entry = "        while (!stoppingToken.IsCancellationRequested)"
        replacement = (
            '        File.AppendAllText("/tmp/startup-trace.txt", "MdbStatusPollingWorker pre-yield\\n");\n'
            "        await Task.Yield();\n"
            '        File.AppendAllText("/tmp/startup-trace.txt", "MdbStatusPollingWorker post-yield\\n");\n'
            + entry
        )
        if source.count(entry) != 1:
            raise RehearsalError("Não foi possível adicionar yield ao worker MDB no diagnóstico.")
        path.write_text(source.replace(entry, replacement, 1), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--segments", type=Path, default=Path("artifacts"))
    parser.add_argument("--previous-ref", default="origin/develop")
    parser.add_argument(
        "--candidate-image",
        help="Também inicia a candidata em paralelo e comprova se a conexão legada permanece ativa.",
    )
    parser.add_argument("--postgres-image", default="postgres:17-alpine")
    parser.add_argument("--startup-timeout-seconds", type=int, default=120)
    parser.add_argument("--startup-trace", action="store_true", help="Insere breadcrumbs temporários na revisão anterior.")
    parser.add_argument(
        "--keep-failed-containers", action="store_true",
        help="Preserva API, PostgreSQL, rede e imagem locais após falha para inspeção manual.",
    )
    parser.add_argument(
        "--skip-database-initialization", action="store_true",
        help="Diagnóstico: ignora a inicialização/migrations e o reset de presença na revisão anterior.",
    )
    parser.add_argument(
        "--disable-hosted-service",
        choices=("TelemetryConnectionManager", "TelemetryDeadlineWorker", "MdbStatusPollingWorker"),
        help="Diagnóstico: omite um worker somente na cópia local da revisão anterior.",
    )
    parser.add_argument(
        "--aspnetcore-urls", default="http://0.0.0.0:8080",
        help="URL de escuta IPv4 explícita no container sintético para coincidir com o probe HTTP.",
    )
    parser.add_argument(
        "--yield-mdb-worker-startup", action="store_true",
        help="Diagnóstico: cede o controle antes da primeira consulta síncrona do worker MDB.",
    )
    parser.add_argument(
        "--schema-mode", choices=("candidate", "baseline"), default="candidate",
        help="Usa o schema candidato aplicado ou deixa a revisão anterior criar seu próprio schema.",
    )
    args = parser.parse_args()

    repo = args.repo.resolve()
    artifacts = args.segments if args.segments.is_absolute() else repo / args.segments
    uid = uuid.uuid4().hex[:10]
    names = {
        "network": f"vendmachines-rollback-net-{uid}",
        "postgres": f"vendmachines-rollback-pg-{uid}",
        "api": f"vendmachines-rollback-api-{uid}",
        "candidate_api": f"vendmachines-candidate-api-{uid}",
        "candidate_api_secondary": f"vendmachines-candidate-api-secondary-{uid}",
        "simulator": f"vendmachines-overlap-simulator-{uid}",
        "image": f"vendmachines-rollback-rehearsal:{uid}",
    }
    password = f"local-rollback-{uid}-Only"
    network_created = postgres_created = api_created = candidate_api_created = False
    candidate_api_secondary_created = simulator_created = image_built = worktree_added = False
    preserve_failed_resources = False
    worktree: Path | None = None
    temp_context: tempfile.TemporaryDirectory[str] | None = None

    try:
        if args.candidate_image:
            run("docker", "image", "inspect", args.candidate_image)
        temp_context = tempfile.TemporaryDirectory(prefix="vendmachines-rollback-")
        temp = Path(temp_context.name)
        worktree = temp / "previous-source"
        print(f"Preparando revisão anterior {args.previous_ref} em worktree temporário...", flush=True)
        run("git", "worktree", "add", "--detach", str(worktree), args.previous_ref, cwd=repo)
        worktree_added = True
        previous_sha = run("git", "rev-parse", "HEAD", cwd=worktree)
        if args.startup_trace:
            add_startup_trace(
                worktree, args.skip_database_initialization, args.disable_hosted_service,
                args.yield_mdb_worker_startup,
            )
            print("Breadcrumbs temporários inseridos no worktree da revisão anterior.", flush=True)
        elif args.skip_database_initialization or args.disable_hosted_service or args.yield_mdb_worker_startup:
            add_startup_trace(
                worktree, args.skip_database_initialization, args.disable_hosted_service,
                args.yield_mdb_worker_startup,
            )
            print("Opções de diagnóstico aplicadas somente à cópia local da revisão anterior.", flush=True)
        print(f"Construindo imagem local da revisão {previous_sha[:12]}...", flush=True)
        run(
            "docker", "build", "--tag", names["image"], "--file",
            str(worktree / "apps/backend/VendingMachines.Api/Dockerfile"),
            str(worktree / "apps/backend/VendingMachines.Api"),
        )
        image_built = True
        run("docker", "network", "create", names["network"])
        network_created = True
        print("Criando PostgreSQL descartável e aplicando os segmentos candidatos...", flush=True)
        run(
            "docker", "run", "--detach", "--network", names["network"],
            "--network-alias", "rollback-postgres", "--name", names["postgres"],
            "--env", f"POSTGRES_PASSWORD={password}", args.postgres_image,
        )
        postgres_created = True

        ready = False
        for _ in range(60):
            # The image's bootstrap server accepts Unix-socket connections only.
            # TCP readiness waits for the final server after bootstrap restarts it.
            code, _ = attempt("docker", "exec", names["postgres"], "pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "postgres")
            if code == 0:
                ready = True
                break
            time.sleep(1)
        if not ready:
            raise RehearsalError("PostgreSQL descartável não ficou pronto.")
        run("docker", "exec", names["postgres"], "createdb", "-U", "postgres", "vendmachines")
        if args.candidate_image:
            configure_postgres_tls(names["postgres"], temp)
        company_id = str(uuid.uuid4())
        machine_id = f"rollback-synthetic-{uid}"
        serial = f"RB-{uid}".upper()
        connection_id = f"old-binary-socket-{uid}"
        presence_query = f'''SELECT CASE WHEN "IsOnline" THEN 'online' ELSE 'offline' END || ':' || COALESCE("ConnectionId",'null') || ':' || COALESCE("OwnerNodeId",'null') FROM "MachineConnectionStates" WHERE "MachineId"='{machine_id}';'''
        if args.schema_mode == "candidate":
            run("docker", "exec", names["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "vendmachines", "-c", "CREATE EXTENSION IF NOT EXISTS pgcrypto;")
            migrations_before = apply_candidate_schema(repo, artifacts, names["postgres"], temp)
            fixture = f'''INSERT INTO "Companies" ("Id","Name","CreatedByUserId","CreatedAt","Cnpj","NormalizedCnpj","ActivationStatus")
SELECT '{company_id}'::uuid,'Synthetic rollback company',"Id",CURRENT_TIMESTAMP,'00.000.000/0000-00','00000000000000','Active' FROM "Users" WHERE "IsActive"=TRUE LIMIT 1;
INSERT INTO "Machines" ("Id","Name","ClientName","Location","Status","IsActive","StockLevel","Revenue30d","TotalSales30d","LastSync","SerialNumber","NormalizedSerialNumber","CompanyId","MercadoPagoEnabled","AutoOpenSessionEnabled")
VALUES ('{machine_id}','Synthetic rollback machine','Fixture','Fixture','online',TRUE,0,0,0,'','{serial}','{serial}','{company_id}',FALSE,FALSE);
INSERT INTO "MachineConnectionStates" ("Id","MachineId","CompanyId","MonitoringEnabled","IsOnline","ConnectionId","ConnectedAt","LastSeenAt","OwnerNodeId","OwnerConnectionId","OwnerLeaseExpiresAt","CommandRoutingVersion")
VALUES (gen_random_uuid(),'{machine_id}','{company_id}',TRUE,TRUE,'{connection_id}',CURRENT_TIMESTAMP,CURRENT_TIMESTAMP,'candidate-node','{connection_id}',CURRENT_TIMESTAMP + INTERVAL '5 minutes',1);'''
            run("docker", "exec", names["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "vendmachines", "-c", fixture)
            before = query(names["postgres"], "vendmachines", presence_query)
            if before != f"online:{connection_id}:candidate-node":
                raise RehearsalError(f"Fixture MDB inesperado antes do startup anterior: {before}")
        else:
            migrations_before = "0"
            before = "(schema vazio, sem presença MDB semeada)"
            print("Controle baseline: sem aplicar segmentos candidatos; a revisão anterior fará suas migrations.", flush=True)

        connection = f"Host=rollback-postgres;Port=5432;Database=vendmachines;Username=postgres;Password={password};SSL Mode=Disable"
        runtime = (
            "ASPNETCORE_ENVIRONMENT=Production",
            "DOTNET_ENVIRONMENT=Production",
            f"ConnectionStrings__DefaultConnection={connection.replace('SSL Mode=Disable', 'SSL Mode=Require;Trust Server Certificate=true')}",
            "Encryption__Key=LocalRollbackEncryptionCurrentKey2026",
            "Encryption__LegacyKey=LocalRollbackEncryptionLegacyKey2026",
            "Encryption__WriteLegacyFormat=false",
            "Jwt__Key=LocalRollbackSigningKeyOnly-NotASecret-2026-123456",
            "MercadoPago__ClientId=LOCAL-ROLLBACK-CLIENT",
            "MercadoPago__ClientSecret=local-rollback-client-secret",
            "MercadoPago__WebhookSecret=local-rollback-webhook-secret",
            "MercadoPago__WebhookUrl=https://rollback.invalid/hooks/mercadopago",
            "Cors__AllowedOrigins__0=https://rollback.invalid",
            "WebSockets__AllowedOrigins__0=https://rollback.invalid",
            "AccountEmail__Enabled=false",
            "Telemetry__RequireDeviceCredentials=false",
            "Telemetry__LegacyApiBaseUrl=http://previous-api:8080",
            "Logging__LogLevel__Default=Debug",
            "Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information",
            f"ASPNETCORE_URLS={args.aspnetcore_urls}",
        )
        command = [
            "docker", "run", "--detach", "--network", names["network"],
            "--network-alias", "previous-api", "--name", names["api"],
            ]
        for value in runtime:
            # Only the candidate relays payments to the previous API. Giving
            # the previous binary the same base URL makes it relay back to
            # itself, where the loop guard correctly returns 508.
            if value.startswith("Telemetry__LegacyApiBaseUrl="):
                continue
            command.extend(("--env", value))
        command.append(names["image"])
        run(*command)
        api_created = True

        health_url = "http://previous-api:8080/health"
        healthy = False
        startup_probe_started = time.monotonic()
        startup_probe_attempts = 0
        last_container_status = "unknown"
        for _ in range(args.startup_timeout_seconds):
            startup_probe_attempts += 1
            probe_code, _ = attempt("docker", "exec", names["postgres"], "wget", "-q", "-O", "-", health_url)
            if args.startup_trace and startup_probe_attempts <= 5:
                print(f"/health probe {startup_probe_attempts}: exit={probe_code}", flush=True)
            if probe_code == 0:
                healthy = True
                break
            inspect_code, container_status = attempt(
                "docker", "inspect", "--format", "{{.State.Status}}", names["api"]
            )
            last_container_status = container_status.strip().lower()
            if args.startup_trace and startup_probe_attempts <= 5:
                print(
                    f"container status after probe {startup_probe_attempts}: "
                    f"inspect_exit={inspect_code} state={last_container_status!r}",
                    flush=True,
                )
            if inspect_code != 0 or last_container_status in {
                "exited", "dead", "removing", "paused"
            }:
                break
            time.sleep(1)
        if not healthy:
            startup_probe_elapsed = time.monotonic() - startup_probe_started
            _, logs = attempt("docker", "logs", "--tail", "300", names["api"])
            _, state = attempt("docker", "inspect", "--format", "{{json .State}}", names["api"])
            _, connectivity = attempt("docker", "exec", names["postgres"], "wget", "-S", "-O", "-", "-T", "3", health_url)
            _, api_loopback_http = attempt(
                "docker", "exec", names["api"], "bash", "-ec",
                "exec 3<>/dev/tcp/127.0.0.1/8080; "
                "printf 'GET /health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3; "
                "IFS= read -r response <&3; printf '%s\\n' \"$response\"",
            )
            _, processes = attempt("docker", "top", names["api"])
            _, startup_trace = attempt("docker", "exec", names["api"], "cat", "/tmp/startup-trace.txt")
            _, api_sockets = attempt("docker", "exec", names["api"], "cat", "/proc/net/tcp")
            _, api_sockets_v6 = attempt("docker", "exec", names["api"], "cat", "/proc/net/tcp6")
            _, api_threads = attempt(
                "docker", "exec", names["api"], "sh", "-c",
                "printf '%s\\n' 'PID 1 status:'; cat /proc/1/status; "
                "printf '%s\\n' 'Thread wait channels:'; "
                "for task in /proc/1/task/*; do printf '%s ' \"${task##*/}\"; "
                "cat \"$task/wchan\"; done",
            )
            _, database_dns = attempt("docker", "exec", names["api"], "getent", "hosts", "rollback-postgres")
            _, database_socket = attempt(
                "docker", "exec", names["api"], "bash", "-ec",
                "timeout 3 bash -ec '</dev/tcp/rollback-postgres/5432' && echo connected",
            )
            _, raw_environment = attempt("docker", "inspect", "--format", "{{json .Config.Env}}", names["api"])
            environment = json.loads(raw_environment)
            safe_environment = []
            for entry in environment:
                key, _, value = entry.partition("=")
                if key == "ConnectionStrings__DefaultConnection":
                    value = re.sub(r"(?i)(password\s*=\s*)[^;]+", r"\1[redacted]", value)
                    safe_environment.append(f"{key}={value}")
                elif key in {"ASPNETCORE_URLS", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "UseSqlite"}:
                    safe_environment.append(entry)
            _, database_sessions = attempt(
                "docker", "exec", names["postgres"], "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines",
                "-c", "SELECT pid || ':' || state || ':' || COALESCE(wait_event_type,'') || ':' || COALESCE(wait_event,'') || ':' || COALESCE((clock_timestamp()-query_start)::text,'') || ':' || COALESCE((clock_timestamp()-xact_start)::text,'') || ':' || left(query,180) FROM pg_stat_activity WHERE datname='vendmachines' AND pid <> pg_backend_pid() ORDER BY pid;",
            )
            _, database_locks = attempt(
                "docker", "exec", names["postgres"], "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines",
                "-c", "SELECT a.pid || ':' || a.state || ':' || l.locktype || ':' || l.mode || ':' || l.granted || ':' || COUNT(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.datname='vendmachines' AND a.pid <> pg_backend_pid() GROUP BY a.pid,a.state,l.locktype,l.mode,l.granted ORDER BY a.pid,l.granted,l.locktype;",
            )
            _, migration_progress = attempt(
                "docker", "exec", names["postgres"], "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines",
                "-c", "SET statement_timeout = '3000ms'; SELECT COUNT(*)::text || ':' || COALESCE(MAX(\"MigrationId\"),'none') FROM \"__EFMigrationsHistory\";",
            )
            _, presence_table = attempt(
                "docker", "exec", names["postgres"], "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines",
                "-c", "SELECT to_regclass('public.\"MachineConnectionStates\"');",
            )
            if presence_table.strip() in {'"MachineConnectionStates"', "public.\"MachineConnectionStates\""}:
                _, presence_state = attempt(
                    "docker", "exec", names["postgres"], "psql", "-X", "-tA", "-U", "postgres", "-d", "vendmachines",
                    "-c", presence_query,
                )
            else:
                presence_state = "table-missing-before-API-startup"
            for value in (password, "local-rollback-client-secret", "local-rollback-webhook-secret"):
                logs = logs.replace(value, "[redacted]")
            print("Logs finais do container anterior (valores sintéticos redigidos):", file=sys.stderr)
            print(
                f"Sondas de /health: {startup_probe_attempts}/{args.startup_timeout_seconds} "
                f"em {startup_probe_elapsed:.1f}s; estado observado={last_container_status}",
                file=sys.stderr,
            )
            print(logs, file=sys.stderr)
            print(f"HTTP probe: {connectivity}", file=sys.stderr)
            print(f"HTTP pela loopback IPv4 do container API: {api_loopback_http}", file=sys.stderr)
            print(f"Processos: {processes}", file=sys.stderr)
            print(f"Etapas de startup registradas no arquivo temporário: {startup_trace}", file=sys.stderr)
            print(f"Threads/wchan do processo da API: {api_threads}", file=sys.stderr)
            print(f"Sockets TCP do container: {api_sockets}", file=sys.stderr)
            print(f"Sockets TCP6 do container: {api_sockets_v6}", file=sys.stderr)
            print(f"DNS do PostgreSQL visto pela API: {database_dns}", file=sys.stderr)
            print(f"TCP do PostgreSQL visto pela API: {database_socket}", file=sys.stderr)
            print(f"Configuração de conexão sem senha: {safe_environment}", file=sys.stderr)
            print(f"Sessões PostgreSQL: {database_sessions}", file=sys.stderr)
            print(f"Locks PostgreSQL: {database_locks}", file=sys.stderr)
            print(f"Progresso do histórico de migrations (contagem:última ID): {migration_progress}", file=sys.stderr)
            print(f"Presença após tentativa: {presence_state}", file=sys.stderr)
            raise RehearsalError(f"O binário anterior não ficou saudável no schema {args.schema_mode}. Estado: {state}")

        after = query(names["postgres"], "vendmachines", presence_query)
        migrations_after = query(names["postgres"], "vendmachines", 'SELECT COUNT(*) FROM "__EFMigrationsHistory";')
        print(f"previous_ref={args.previous_ref} previous_sha={previous_sha}")
        print(f"schema_mode={args.schema_mode}")
        print(f"migrations_before={migrations_before} migrations_after={migrations_after}")
        print(f"synthetic_presence_before={before}")
        if args.schema_mode == "candidate":
            print(f"synthetic_presence_after={after}")
        if args.schema_mode == "candidate" and migrations_before != migrations_after:
            raise RehearsalError("O startup anterior alterou __EFMigrationsHistory.")
        previous_startup = (worktree / "apps/backend/VendingMachines.Api/Program.cs").read_text(encoding="utf-8")
        resets_presence = "context.MachineConnectionStates.ExecuteUpdate(" in previous_startup
        expected_presence = "offline:null:null" if resets_presence else before
        if args.schema_mode == "candidate" and not args.skip_database_initialization and after != expected_presence:
            raise RehearsalError(f"Startup anterior inesperado: {after}; esperado: {expected_presence}")
        if args.schema_mode == "baseline":
            print("Controle baseline saudável.")
        elif args.skip_database_initialization:
            print("Host da revisão anterior saudável com inicialização do banco ignorada para diagnóstico.")
        else:
            behavior = "zerada" if resets_presence else "preservada"
            print(f"Startup anterior compatível com o schema; presença MDB {behavior} conforme esperado.")

        if args.candidate_image:
            if args.schema_mode != "candidate" or args.skip_database_initialization:
                raise RehearsalError("O ensaio de sobreposição exige o schema candidato e a inicialização normal da API anterior.")

            simulator_project = repo / "tools/VendingMachines.Simulator/VendingMachines.Simulator.csproj"
            simulator_build = run(
                "dotnet", "build", str(simulator_project), "--configuration", "Release", "--verbosity", "quiet",
                cwd=repo,
            )
            simulator_assembly = repo / "tools/VendingMachines.Simulator/bin/Release/net10.0/vend-machine.dll"
            simulator_output = simulator_assembly.parent
            if not simulator_assembly.is_file():
                raise RehearsalError("O build do simulador terminou sem gerar vend-machine.dll.")

            simulator_command = [
                "docker", "create", "--network", names["network"], "--name", names["simulator"],
                "--entrypoint", "dotnet", args.candidate_image, "/tmp/vend-machine.dll",
                "--serial", serial, "--url", "ws://previous-api:8080/telemetria",
                "--heartbeat", "5", "--headless",
            ]
            run(*simulator_command)
            run("docker", "cp", f"{simulator_output}/.", f"{names['simulator']}:/tmp/")
            run("docker", "start", names["simulator"])
            simulator_created = True

            live_presence_query = (
                f'''SELECT COALESCE("ConnectionId",'') || '|' || "IsOnline"::text || '|' || '''
                f'''COALESCE("OwnerNodeId",'') FROM "MachineConnectionStates" '''
                f'''WHERE "MachineId"='{machine_id}';'''
            )
            live_presence = ""
            for _ in range(45):
                live_presence = query(names["postgres"], "vendmachines", live_presence_query)
                if (live_presence.count("|") == 2 and live_presence.split("|")[1] == "true"
                        and live_presence.split("|")[0] not in {"", connection_id}):
                    break
                status_code, simulator_state = attempt(
                    "docker", "inspect", "--format", "{{.State.Status}}", names["simulator"],
                )
                if status_code != 0 or simulator_state.strip() != "running":
                    _, simulator_logs = attempt("docker", "logs", names["simulator"])
                    raise RehearsalError(f"O simulador encerrou antes de conectar à API anterior: {simulator_logs[-2500:]}")
                time.sleep(1)
            if (live_presence.count("|") != 2 or live_presence.split("|")[1] != "true"
                    or live_presence.split("|")[0] in {"", connection_id}):
                _, simulator_logs = attempt("docker", "logs", names["simulator"])
                raise RehearsalError(f"O simulador não conectou à API anterior. presence={live_presence}; logs={simulator_logs[-2500:]}")
            old_connection_id = live_presence.split("|")[0]

            readiness_setup = (
                'UPDATE "SessionAdmissionGates" SET "NewSessionsPaused"=FALSE, '
                '"UpdatedAt"=CURRENT_TIMESTAMP,"UpdatedBy"=\'local-overlap\','
                '"Reason"=\'synthetic overlap readiness test\' WHERE "Id"=1; '
                'INSERT INTO "SystemSettings" ("Id","IsSingleton","ApplicationFeePercent","UpdatedAt") '
                'VALUES (gen_random_uuid(),TRUE,5.00,CURRENT_TIMESTAMP);'
            )
            run(
                "docker", "exec", names["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1",
                "-U", "postgres", "-d", "vendmachines", "-c", readiness_setup,
            )

            candidate_command = [
                "docker", "run", "--detach", "--network", names["network"],
                "--network-alias", "candidate-api", "--name", names["candidate_api"],
            ]
            for value in runtime:
                candidate_command.extend(("--env", value))
            candidate_command.append(args.candidate_image)
            run(*candidate_command)
            candidate_api_created = True

            candidate_healthy = False
            for _ in range(args.startup_timeout_seconds):
                probe_code, _ = attempt(
                    "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                    "http://candidate-api:8080/health",
                )
                if probe_code == 0:
                    candidate_healthy = True
                    break
                inspect_code, candidate_state = attempt(
                    "docker", "inspect", "--format", "{{.State.Status}}", names["candidate_api"],
                )
                if inspect_code != 0 or candidate_state.strip().lower() in {"exited", "dead", "removing"}:
                    _, candidate_logs = attempt("docker", "logs", names["candidate_api"])
                    raise RehearsalError(f"A candidata falhou ao iniciar sobre o schema compartilhado: {candidate_logs[-3500:]}")
                time.sleep(1)
            if not candidate_healthy:
                _, candidate_logs = attempt("docker", "logs", "--tail", "100", names["candidate_api"])
                raise RehearsalError(f"A candidata não ficou saudável na sobreposição: {candidate_logs[-3500:]}")

            secondary_candidate_command = [
                "docker", "run", "--detach", "--network", names["network"],
                "--network-alias", "candidate-api-secondary", "--name", names["candidate_api_secondary"],
            ]
            for value in runtime:
                secondary_candidate_command.extend(("--env", value))
            secondary_candidate_command.append(args.candidate_image)
            run(*secondary_candidate_command)
            candidate_api_secondary_created = True

            secondary_candidate_healthy = False
            for _ in range(args.startup_timeout_seconds):
                probe_code, _ = attempt(
                    "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                    "http://candidate-api-secondary:8080/health",
                )
                if probe_code == 0:
                    secondary_candidate_healthy = True
                    break
                inspect_code, candidate_state = attempt(
                    "docker", "inspect", "--format", "{{.State.Status}}", names["candidate_api_secondary"],
                )
                if inspect_code != 0 or candidate_state.strip().lower() in {"exited", "dead", "removing"}:
                    _, candidate_logs = attempt("docker", "logs", names["candidate_api_secondary"])
                    raise RehearsalError(
                        f"A segunda candidata falhou ao iniciar sobre o schema compartilhado: {candidate_logs[-3500:]}"
                    )
                time.sleep(1)
            if not secondary_candidate_healthy:
                _, candidate_logs = attempt("docker", "logs", "--tail", "100", names["candidate_api_secondary"])
                raise RehearsalError(
                    f"A segunda candidata não ficou saudável na sobreposição: {candidate_logs[-3500:]}"
                )

            readiness_code, readiness_body = attempt(
                "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                "http://candidate-api:8080/health/deploy-ready",
            )
            if readiness_code != 0:
                owner_routing_version = query(
                    names["postgres"], "vendmachines",
                    f'''SELECT COALESCE("CommandRoutingVersion"::text, 'null') FROM "MachineConnectionStates" WHERE "MachineId"='{machine_id}';''',
                )
                if owner_routing_version != "1":
                    raise RehearsalError(
                        "O deploy só deve ser bloqueado nesta etapa pelo contrato inseguro do owner legado; "
                        f"version={owner_routing_version}, exit={readiness_code}, body={readiness_body[-1800:]}"
                    )
                print("candidate_deploy_readiness=blocked_legacy_closure_guard")
            else:
                try:
                    readiness_payload = json.loads(readiness_body)
                except json.JSONDecodeError as exc:
                    raise RehearsalError(f"Readiness retornou JSON inválido: {readiness_body[-1800:]}") from exc
                if readiness_payload.get("status") != "Ready":
                    raise RehearsalError(
                        "A candidata não reconheceu a API anterior saudável como owner compatível; "
                        f"body={readiness_body[-1800:]}"
                    )
                print("candidate_deploy_readiness=ready")

            admin_user_id = query(
                names["postgres"], "vendmachines",
                'SELECT "Id"::text FROM "Users" WHERE "IsActive"=TRUE AND "Role"=\'PlatformAdmin\' ORDER BY "CreatedAt" LIMIT 1;',
            )
            if not admin_user_id:
                raise RehearsalError("Não foi encontrada conta sintética PlatformAdmin para os fluxos de sobreposição.")
            test_token = create_test_jwt(
                "LocalRollbackSigningKeyOnly-NotASecret-2026-123456", admin_user_id, company_id,
            )
            authorization_header = f"--header=Authorization: Bearer {test_token}"

            concurrent_start_setup = (
                "CREATE OR REPLACE FUNCTION local_delay_legacy_session_start() RETURNS trigger "
                "LANGUAGE plpgsql AS $$ BEGIN "
                f"IF NEW.\"MachineId\"='{machine_id}' THEN PERFORM pg_sleep(1); END IF; "
                "RETURN NEW; END; $$; "
                "CREATE TRIGGER local_delay_legacy_session_start BEFORE INSERT ON \"MachineSessions\" "
                "FOR EACH ROW EXECUTE FUNCTION local_delay_legacy_session_start();"
            )
            run(
                "docker", "exec", names["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1",
                "-U", "postgres", "-d", "vendmachines", "-c", concurrent_start_setup,
            )
            start_request_commands = []
            for candidate_host in ("candidate-api", "candidate-api-secondary"):
                for _ in range(2):
                    start_request_commands.append((
                        "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                        authorization_header,
                        "--header=Content-Type: application/json",
                        "--post-data={}",
                        f"http://{candidate_host}:8080/api/telemetry/machines/{machine_id}/sessions",
                    ))
            with ThreadPoolExecutor(max_workers=len(start_request_commands)) as executor:
                start_results = list(executor.map(lambda command: attempt(*command), start_request_commands))
            start_payloads = []
            concurrent_conflicts = 0
            for start_index, (start_code, start_response) in enumerate(start_results, start=1):
                if start_code != 0:
                    # The start endpoint preserves its existing 409 conflict
                    # behavior once another concurrent request has registered
                    # the single active session. The invariant under test is
                    # that the race creates only one session and one command.
                    if "HTTP/1.1 409 Conflict" in start_response:
                        concurrent_conflicts += 1
                        continue
                    raise RehearsalError(
                        f"Início de sessão concorrente {start_index} falhou; "
                        f"exit={start_code}, body={start_response[-1800:]}"
                    )
                try:
                    start_payload = json.loads(start_response)
                    start_payload["id"] = str(uuid.UUID(start_payload["id"]))
                    start_payloads.append(start_payload)
                except (json.JSONDecodeError, KeyError, ValueError, TypeError) as exc:
                    raise RehearsalError(
                        f"Início de sessão concorrente {start_index} retornou JSON inválido: {start_response[-1800:]}"
                    ) from exc
            if not start_payloads:
                raise RehearsalError("Nenhum início concorrente foi aceito; todas as tentativas foram rejeitadas.")
            run(
                "docker", "exec", names["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1",
                "-U", "postgres", "-d", "vendmachines", "-c",
                "DROP TRIGGER local_delay_legacy_session_start ON \"MachineSessions\"; "
                "DROP FUNCTION local_delay_legacy_session_start();",
            )
            relayed_session_id = start_payloads[0]["id"]
            active_session_count = query(
                names["postgres"], "vendmachines",
                f'''SELECT COUNT(*) FROM "MachineSessions" WHERE "MachineId"='{machine_id}' AND "ClosedAt" IS NULL;''',
            )
            open_command_count = query(
                names["postgres"], "vendmachines",
                f'''SELECT COUNT(*) FROM "TelemetryCommands" WHERE "SessionId"='{relayed_session_id}'::uuid AND "Command"='ABRIR_SESSAO';''',
            )
            if {payload["id"] for payload in start_payloads} != {relayed_session_id} or \
                active_session_count != "1" or open_command_count != "1":
                raise RehearsalError(
                    "Inícios simultâneos pelas candidatas devem retornar a mesma sessão e emitir um comando; "
                    f"returned_ids={[payload['id'] for payload in start_payloads]}, "
                    f"active_sessions={active_session_count}, open_commands={open_command_count}"
                )
            print(f"candidate_concurrent_legacy_session_start_conflicts={concurrent_conflicts}")
            print("candidate_concurrent_legacy_session_start_single_open_command=yes")
            print("candidate_legacy_session_start=accepted")

            duplicate_start_code, duplicate_start_response = attempt(
                "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                authorization_header,
                "--header=Content-Type: application/json",
                "--post-data={}",
                f"http://candidate-api:8080/api/telemetry/machines/{machine_id}/sessions",
            )
            active_session_count = query(
                names["postgres"], "vendmachines",
                f'''SELECT COUNT(*) FROM "MachineSessions" WHERE "MachineId"='{machine_id}' AND "ClosedAt" IS NULL;''',
            )
            open_command_count = query(
                names["postgres"], "vendmachines",
                f'''SELECT COUNT(*) FROM "TelemetryCommands" WHERE "SessionId"='{relayed_session_id}'::uuid AND "Command"='ABRIR_SESSAO';''',
            )
            duplicate_start_reused = False
            if duplicate_start_code == 0:
                try:
                    duplicate_start_payload = json.loads(duplicate_start_response)
                except json.JSONDecodeError as exc:
                    raise RehearsalError(
                        "O retry do início de sessão retornou JSON inválido: "
                        f"{duplicate_start_response[-1200:]}"
                    ) from exc
                duplicate_start_reused = duplicate_start_payload.get("id") == relayed_session_id
            duplicate_start_conflicted = "HTTP/1.1 409 Conflict" in duplicate_start_response
            if not (duplicate_start_reused or duplicate_start_conflicted) or \
                active_session_count != "1" or open_command_count != "1":
                raise RehearsalError(
                    "O retry do início de sessão deveria reutilizar ou conflitar com a sessão existente, "
                    "sem criar sessão ou comando duplicado; "
                    f"http_exit={duplicate_start_code}, reused={duplicate_start_reused}, "
                    f"conflict={duplicate_start_conflicted}, "
                    f"active_sessions={active_session_count}, "
                    f"open_commands={open_command_count}, response={duplicate_start_response[-1200:]}"
                )
            print(f"candidate_legacy_duplicate_session_start_reused={str(duplicate_start_reused).lower()}")
            print(f"candidate_legacy_duplicate_session_start_conflict={str(duplicate_start_conflicted).lower()}")
            print("candidate_legacy_duplicate_session_start_no_second_command=yes")

            mdb_code, mdb_response = attempt(
                "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                authorization_header,
                "--post-data=",
                f"http://candidate-api:8080/api/telemetry/machines/{machine_id}/mdb-status",
            )
            if mdb_code != 0:
                raise RehearsalError(
                    "A candidata não encaminhou a consulta manual MDB ao owner legado; "
                    f"exit={mdb_code}, body={mdb_response[-1800:]}"
                )
            try:
                mdb_payload = json.loads(mdb_response)
            except json.JSONDecodeError as exc:
                raise RehearsalError(f"Relay MDB retornou resposta inválida: {mdb_response[-1800:]}") from exc
            if mdb_payload.get("machineId") != machine_id or not mdb_payload.get("messageId"):
                raise RehearsalError(f"Resposta MDB não confirmou correlação da consulta: {mdb_response[-1800:]}")
            print("candidate_legacy_mdb_status=correlated")

            cancel_delay_trigger = (
                "CREATE OR REPLACE FUNCTION local_delay_legacy_session_close() RETURNS trigger "
                "LANGUAGE plpgsql AS $$ BEGIN "
                f"IF NEW.\"Id\"='{relayed_session_id}'::uuid AND NEW.\"State\"='ClosingWithoutSelection' "
                "THEN PERFORM pg_sleep(1); END IF; RETURN NEW; END; $$; "
                "CREATE TRIGGER local_delay_legacy_session_close BEFORE UPDATE OF \"State\" ON \"MachineSessions\" "
                "FOR EACH ROW EXECUTE FUNCTION local_delay_legacy_session_close();"
            )
            run(
                "docker", "exec", names["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1",
                "-U", "postgres", "-d", "vendmachines", "-c", cancel_delay_trigger,
            )
            cancel_request_commands = []
            for candidate_host in ("candidate-api", "candidate-api-secondary"):
                for _ in range(2):
                    cancel_request_commands.append((
                        "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                        authorization_header,
                        "--header=Content-Type: application/json",
                        "--post-data={\"reason\":\"local_overlap_concurrent_cancel\"}",
                        f"http://{candidate_host}:8080/api/telemetry/sessions/{relayed_session_id}/cancel",
                    ))
            with ThreadPoolExecutor(max_workers=len(cancel_request_commands)) as executor:
                cancel_results = list(executor.map(lambda command: attempt(*command), cancel_request_commands))

            for cancel_index, (cancel_code, cancel_response) in enumerate(cancel_results, start=1):
                if cancel_code == 0 or "HTTP/1.1 503 Service Unavailable" not in cancel_response:
                    raise RehearsalError(
                        f"Cancelamento concorrente {cancel_index} não foi pausado para o owner legado; "
                        f"exit={cancel_code}, body={cancel_response[-1800:]}"
                    )

            close_command_count = query(
                names["postgres"], "vendmachines",
                f'''SELECT COUNT(*) FROM "TelemetryCommands" WHERE "SessionId"='{relayed_session_id}'::uuid AND "Command"='FECHAR_SESSAO';''',
            )
            run(
                "docker", "exec", names["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1",
                "-U", "postgres", "-d", "vendmachines", "-c",
                "DROP TRIGGER local_delay_legacy_session_close ON \"MachineSessions\"; "
                "DROP FUNCTION local_delay_legacy_session_close();",
            )
            pending_session_state = query(
                names["postgres"], "vendmachines",
                f'''SELECT "State" || '|' || CASE WHEN "ClosedAt" IS NULL THEN 'open' ELSE 'closed' END '''
                f'''FROM "MachineSessions" WHERE "Id"='{relayed_session_id}'::uuid;''',
            )
            if close_command_count != "0" or pending_session_state != "AwaitingSelection|open":
                raise RehearsalError(
                    "Um owner legado sem a proteção MDB precisa manter o fechamento pendente sem comando; "
                    f"FECHAR_SESSAO={close_command_count}, session={pending_session_state}"
                )
            print("candidate_concurrent_legacy_cancel_no_close_command=yes")
            print("candidate_legacy_session_cancel=paused_for_incompatible_owner")
            run(
                "docker", "exec", names["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1",
                "-U", "postgres", "-d", "vendmachines", "-c",
                f'''UPDATE "MachineSessions" SET "State"='ClosedWithoutSelection', "ClosedAt"=CURRENT_TIMESTAMP, "CloseReason"='synthetic_overlap_cleanup' WHERE "Id"='{relayed_session_id}'::uuid;''',
            )
            run("docker", "kill", names["candidate_api_secondary"])

            legacy_payment_id = str(int(uid[:8], 16))
            legacy_transaction_id = str(uuid.uuid4())
            legacy_session_id = str(uuid.uuid4())
            legacy_webhook_setup = (
                'INSERT INTO "PaymentTransactions" '
                '("Id","MachineId","CompanyId","Amount","ApplicationFee","Status",'
                '"SendTelemetryToMachine","MercadoPagoPaymentId","QrCode","QrCodeBase64","CreatedAt") '
                f"VALUES ('{legacy_transaction_id}'::uuid,'{machine_id}','{company_id}'::uuid,10.00,0.50,"
                f"'Pending',1,'{legacy_payment_id}','fixture','',CURRENT_TIMESTAMP); "
                'INSERT INTO "MachineSessions" '
                '("Id","MachineId","CompanyId","Source","State","TransactionId","StartedAt","LastEventAt") '
                f"VALUES ('{legacy_session_id}'::uuid,'{machine_id}','{company_id}'::uuid,'panel',"
                f"'PaymentPending','{legacy_transaction_id}'::uuid,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP);"
            )
            run(
                "docker", "exec", names["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1",
                "-U", "postgres", "-d", "vendmachines", "-c", legacy_webhook_setup,
            )
            webhook_request_id = f"legacy-webhook-{uid}"
            webhook_timestamp = str(int(time.time()))
            webhook_manifest = (
                f"id:{legacy_payment_id};request-id:{webhook_request_id};ts:{webhook_timestamp};"
            )
            webhook_signature = hmac.new(
                b"local-rollback-webhook-secret", webhook_manifest.encode("utf-8"), hashlib.sha256,
            ).hexdigest()
            webhook_body = json.dumps({
                "type": "payment",
                "action": "payment.updated",
                "data": {"id": legacy_payment_id},
            }, separators=(",", ":"))
            webhook_code, webhook_response = attempt(
                "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                f"--header=x-signature: ts={webhook_timestamp},v1={webhook_signature}",
                f"--header=x-request-id: {webhook_request_id}",
                "--header=Content-Type: application/json",
                f"--post-data={webhook_body}",
                f"http://candidate-api:8080/webhooks/mercadopago?data.id={legacy_payment_id}&type=payment",
            )
            if webhook_code != 0:
                raise RehearsalError(
                    "A candidata não encaminhou o webhook do pagamento ao owner legado; "
                    f"exit={webhook_code}, body={webhook_response[-1800:]}"
                )
            try:
                webhook_payload = json.loads(webhook_response)
            except json.JSONDecodeError as exc:
                raise RehearsalError(f"Relay retornou resposta de webhook inválida: {webhook_response[-1800:]}") from exc
            if webhook_payload.get("routedToLegacyOwner") is not True:
                raise RehearsalError(
                    "A candidata respondeu sem confirmar o encaminhamento ao owner legado; "
                    f"body={webhook_response[-1800:]}"
                )
            legacy_webhook_inbox_query = (
                f"SELECT COUNT(*) FROM \"WebhookInboxMessages\" WHERE \"PaymentId\"='{legacy_payment_id}';"
            )
            legacy_webhook_inbox_count = "0"
            for _ in range(10):
                legacy_webhook_inbox_count = query(names["postgres"], "vendmachines", legacy_webhook_inbox_query)
                if legacy_webhook_inbox_count != "0":
                    break
                time.sleep(1)
            if legacy_webhook_inbox_count == "0":
                _, previous_webhook_logs = attempt("docker", "logs", "--tail", "120", names["api"])
                raise RehearsalError(
                    "A API antiga respondeu ao relay, mas não persistiu o webhook na inbox do owner. "
                    f"Logs da API anterior: {previous_webhook_logs[-5000:]}"
                )
            candidate_inbox_count = query(names["postgres"], "vendmachines", legacy_webhook_inbox_query)
            if candidate_inbox_count != "1":
                raise RehearsalError(
                    "O webhook legado deve persistir exatamente uma vez na inbox compartilhada; "
                    f"encontrados {candidate_inbox_count} registros."
                )
            print("legacy_payment_webhook_routed_to_owner=yes")
            print("legacy_payment_webhook_owner_inbox=single_record")

            request_id = str(uuid.uuid4())
            command_body = json.dumps({
                "requestId": request_id,
                "command": "VENDMACHINES_CONTINUITY_PROBE",
                "data": "VENDMACHINES_CONTINUITY_PROBE",
            }, separators=(",", ":"))
            command_code, command_response = attempt(
                "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                authorization_header,
                "--header=Content-Type: application/json",
                f"--post-data={command_body}",
                f"http://candidate-api:8080/api/telemetry/machines/{machine_id}/commands",
            )
            if command_code != 0:
                raise RehearsalError(
                    "A candidata não conseguiu encaminhar um comando autenticado ao processo que mantém o socket; "
                    f"exit={command_code}, body={command_response[-1800:]}"
                )
            try:
                command_payload = json.loads(command_response)
            except json.JSONDecodeError as exc:
                raise RehearsalError(f"Relay retornou resposta de comando inválida: {command_response[-1800:]}") from exc
            if command_payload.get("status") != "acknowledged":
                raise RehearsalError(
                    "O processo antigo não confirmou o comando encaminhado pela candidata; "
                    f"body={command_response[-1800:]}"
                )

            time.sleep(7)
            live_presence_after = query(names["postgres"], "vendmachines", live_presence_query)
            _, simulator_logs = attempt("docker", "logs", names["simulator"])
            if (
                live_presence_after.split("|")[0] != old_connection_id
                or live_presence_after.split("|")[1] != "true"
                or "pong id=" not in simulator_logs
            ):
                raise RehearsalError(
                    "A conexão antiga não permaneceu estável durante o startup candidato. "
                    f"before={live_presence}, after={live_presence_after}, "
                    f"simulator_logs={simulator_logs[-2500:]}"
                )
            print("overlap_socket_preserved=yes")
            print("candidate_readiness=Ready_with_healthy_legacy_owner_relay")
            print("candidate_can_route_legacy_socket=yes")
            print("candidate_legacy_command_relay=acknowledged")

            run("docker", "kill", names["candidate_api"])
            time.sleep(6)
            legacy_presence_during_candidate_stop = query(
                names["postgres"], "vendmachines", live_presence_query,
            )
            simulator_state_code, simulator_state = attempt(
                "docker", "inspect", "--format", "{{.State.Status}}", names["simulator"],
            )
            _, simulator_logs = attempt("docker", "logs", names["simulator"])
            if (
                legacy_presence_during_candidate_stop.split("|")[0] != old_connection_id
                or legacy_presence_during_candidate_stop.split("|")[1] != "true"
                or simulator_state_code != 0
                or simulator_state.strip() != "running"
                or "pong id=" not in simulator_logs
            ):
                raise RehearsalError(
                    "Parar a candidata afetou o socket MDB mantido pela API antiga. "
                    f"presence={legacy_presence_during_candidate_stop}, simulator={simulator_state}, "
                    f"logs={simulator_logs[-2500:]}"
                )
            print("legacy_socket_survives_candidate_stop=yes")

            overlap_transaction_id = str(uuid.uuid4())
            overlap_session_id = str(uuid.uuid4())
            overlap_outbox_id = str(uuid.uuid4())
            candidate_outbox_fixture = (
                'INSERT INTO "PaymentTransactions" '
                '("Id","MachineId","CompanyId","Amount","ApplicationFee","Status",'
                '"SendTelemetryToMachine","MercadoPagoPaymentId","QrCode","QrCodeBase64","CreatedAt") '
                f"VALUES ('{overlap_transaction_id}'::uuid,'{machine_id}','{company_id}'::uuid,10.00,0.50,"
                f"'Pending',1,'mock_overlap_{uid}','fixture','',CURRENT_TIMESTAMP); "
                'INSERT INTO "MachineSessions" '
                '("Id","MachineId","CompanyId","Source","State","TransactionId",'
                '"StartedAt","LastEventAt","ClosedAt") '
                f"VALUES ('{overlap_session_id}'::uuid,'{machine_id}','{company_id}'::uuid,'panel',"
                f"'RefundPending','{overlap_transaction_id}'::uuid,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP); "
                'INSERT INTO "OutboxMessages" '
                '("Id","CompanyId","MachineId","SessionId","TransactionId","MessageType",'
                '"PayloadJson","Status","Attempts","CreatedAt","NextAttemptAt") '
                f"VALUES ('{overlap_outbox_id}'::uuid,'{company_id}'::uuid,'{machine_id}',"
                f"'{overlap_session_id}'::uuid,'{overlap_transaction_id}'::uuid,'refund_payment','{{}}',"
                "'deferred_legacy_overlap',0,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP);"
            )
            run(
                "docker", "exec", names["postgres"], "psql", "-X", "-v", "ON_ERROR_STOP=1",
                "-U", "postgres", "-d", "vendmachines", "-c", candidate_outbox_fixture,
            )
            time.sleep(6)
            legacy_outbox_status = query(
                names["postgres"], "vendmachines",
                f'''SELECT "Status" FROM "OutboxMessages" WHERE "Id"='{overlap_outbox_id}'::uuid;''',
            )
            if legacy_outbox_status != "deferred_legacy_overlap":
                raise RehearsalError(
                    "A API antiga reivindicou uma outbox candidata enquanto a candidata estava parada; "
                    f"status={legacy_outbox_status}"
                )
            print("legacy_api_ignores_candidate_deferred_outbox=yes")

            run("docker", "start", names["candidate_api"])
            candidate_healthy = False
            for _ in range(args.startup_timeout_seconds):
                probe_code, _ = attempt(
                    "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                    "http://candidate-api:8080/health",
                )
                if probe_code == 0:
                    candidate_healthy = True
                    break
                inspect_code, candidate_state = attempt(
                    "docker", "inspect", "--format", "{{.State.Status}}", names["candidate_api"],
                )
                if inspect_code != 0 or candidate_state.strip().lower() in {"exited", "dead", "removing"}:
                    _, candidate_logs = attempt("docker", "logs", names["candidate_api"])
                    raise RehearsalError(
                        "A candidata falhou ao reiniciar com a API anterior ainda ativa: "
                        f"{candidate_logs[-3500:]}"
                    )
                time.sleep(1)
            if not candidate_healthy:
                _, candidate_logs = attempt("docker", "logs", "--tail", "100", names["candidate_api"])
                raise RehearsalError(
                    "A candidata não ficou saudável após reiniciar com o socket legado ativo: "
                    f"{candidate_logs[-3500:]}"
                )

            post_restart_request_id = str(uuid.uuid4())
            post_restart_command = json.dumps({
                "requestId": post_restart_request_id,
                "command": "VENDMACHINES_POST_RESTART_PROBE",
                "data": "VENDMACHINES_POST_RESTART_PROBE",
            }, separators=(",", ":"))
            post_restart_code, post_restart_response = attempt(
                "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                authorization_header,
                "--header=Content-Type: application/json",
                f"--post-data={post_restart_command}",
                f"http://candidate-api:8080/api/telemetry/machines/{machine_id}/commands",
            )
            try:
                post_restart_payload = json.loads(post_restart_response)
            except json.JSONDecodeError as exc:
                raise RehearsalError(
                    "O relay retornou JSON inválido após reiniciar a candidata: "
                    f"{post_restart_response[-1800:]}"
                ) from exc
            if post_restart_code != 0 or post_restart_payload.get("status") != "acknowledged":
                raise RehearsalError(
                    "O comando não recebeu ACK após reiniciar a candidata com o owner legado ativo: "
                    f"{post_restart_response[-1800:]}"
                )

            time.sleep(6)
            legacy_presence_after_candidate_restart = query(
                names["postgres"], "vendmachines", live_presence_query,
            )
            _, simulator_logs = attempt("docker", "logs", names["simulator"])
            if (
                legacy_presence_after_candidate_restart.split("|")[0] != old_connection_id
                or legacy_presence_after_candidate_restart.split("|")[1] != "true"
                or "pong id=" not in simulator_logs
            ):
                raise RehearsalError(
                    "O socket MDB legado mudou durante o reinício da candidata. "
                    f"before={live_presence}, during_stop={legacy_presence_during_candidate_stop}, "
                    f"after={legacy_presence_after_candidate_restart}, logs={simulator_logs[-2500:]}"
                )

            processed_outbox_state = ""
            for _ in range(30):
                processed_outbox_state = query(
                    names["postgres"], "vendmachines",
                    f'''SELECT "Status" || '|' || "Attempts" FROM "OutboxMessages" WHERE "Id"='{overlap_outbox_id}'::uuid;''',
                )
                if processed_outbox_state == "processed|1":
                    break
                time.sleep(0.5)
            if processed_outbox_state != "processed|1":
                raise RehearsalError(
                    "A candidata reiniciada não processou a outbox exclusiva enquanto a API antiga seguia ativa; "
                    f"status={processed_outbox_state}"
                )
            overlap_financial_state = query(
                names["postgres"], "vendmachines",
                f'''SELECT session."State" || '|' || payment."Status" FROM "MachineSessions" session '''
                f'''JOIN "PaymentTransactions" payment ON payment."Id"=session."TransactionId" '''
                f'''WHERE session."Id"='{overlap_session_id}'::uuid;''',
            )
            if overlap_financial_state != "Refunded|Refunded":
                raise RehearsalError(
                    "A outbox candidata terminou, mas a sessão e o pagamento não foram atualizados juntos; "
                    f"state={overlap_financial_state}"
                )
            print("candidate_financial_outbox_processed_during_overlap=yes")
            print("legacy_socket_survives_candidate_restart=yes")
            print("candidate_post_restart_command_relay=acknowledged")

            restart_readiness_code, restart_readiness_body = attempt(
                "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                "http://candidate-api:8080/health/deploy-ready",
            )
            try:
                restart_readiness = json.loads(restart_readiness_body)
            except json.JSONDecodeError as exc:
                raise RehearsalError(
                    "Readiness retornou JSON inválido após reiniciar a candidata: "
                    f"{restart_readiness_body[-1800:]}"
                ) from exc
            if restart_readiness_code != 0 or restart_readiness.get("status") != "Ready":
                raise RehearsalError(
                    "A candidata não recuperou readiness com a API antiga saudável: "
                    f"{restart_readiness_body[-1800:]}"
                )

            run("docker", "kill", names["api"])
            time.sleep(6)
            unavailable_readiness_code, unavailable_readiness_response = attempt(
                "docker", "exec", names["candidate_api"], "bash", "-ec",
                "exec 3<>/dev/tcp/127.0.0.1/8080; "
                "printf 'GET /health/deploy-ready HTTP/1.0\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3; "
                "cat <&3",
            )
            readiness_body = unavailable_readiness_response.replace("\r\n", "\n").split("\n\n", 1)[-1].strip()
            try:
                unavailable_readiness = json.loads(readiness_body)
            except (json.JSONDecodeError, ValueError) as exc:
                raise RehearsalError(
                    "Readiness da candidata não retornou diagnóstico JSON quando a API proprietária caiu; "
                    f"exit={unavailable_readiness_code}, body={unavailable_readiness_response[-1800:]}"
                ) from exc
            if unavailable_readiness_code != 0 or " 503 " not in unavailable_readiness_response.splitlines()[0] or \
                unavailable_readiness.get("reason") != "legacy_owner_api_unavailable":
                raise RehearsalError(
                    "A candidata não bloqueou readiness após a queda do owner legado; "
                    f"exit={unavailable_readiness_code}, body={unavailable_readiness_response[-1800:]}"
                )
            print("candidate_readiness_blocks_unavailable_legacy_owner=yes")

            failure_request_id = str(uuid.uuid4())
            failure_command_body = json.dumps({
                "requestId": failure_request_id,
                "command": "VENDMACHINES_FAILOVER_PROBE",
                "data": "VENDMACHINES_FAILOVER_PROBE",
            }, separators=(",", ":"))
            failure_command_code, failure_command_response = attempt(
                "docker", "exec", names["postgres"], "wget", "-q", "-O", "-",
                authorization_header,
                "--header=Content-Type: application/json",
                f"--post-data={failure_command_body}",
                f"http://candidate-api:8080/api/telemetry/machines/{machine_id}/commands",
            )
            failure_command_row = ""
            failure_command_query = (
                f'''SELECT "Status" || '|' || COALESCE("Error",'') FROM "TelemetryCommands" '''
                f'''WHERE "Id"='{failure_request_id}'::uuid;'''
            )
            failure_dispatch_count = query(
                names["postgres"], "vendmachines",
                f'''SELECT COUNT(*) FROM "TelemetryCommandDispatches" WHERE "MachineId"='{machine_id}' '''
                "AND \"PayloadJson\" LIKE '%VENDMACHINES_FAILOVER_PROBE%';",
            )
            for _ in range(10):
                failure_command_row = query(names["postgres"], "vendmachines", failure_command_query)
                if failure_command_row:
                    break
                time.sleep(0.5)
            if failure_command_code == 0 or failure_command_row != "failed|command_outcome_unknown" or failure_dispatch_count != "0":
                raise RehearsalError(
                    "A candidata não registrou o resultado incerto do relay sem owner; "
                    f"exit={failure_command_code}, audit={failure_command_row}, dispatches={failure_dispatch_count}, "
                    f"body={failure_command_response[-1800:]}"
                )
            print("candidate_legacy_command_after_owner_loss=failed_closed")
    except (OSError, RehearsalError, subprocess.SubprocessError) as exc:
        print(f"Falha no ensaio local de rollback: {exc}", file=sys.stderr)
        preserve_failed_resources = args.keep_failed_containers
        if preserve_failed_resources:
            print(
                f"Recursos locais preservados para diagnóstico: API={names['api']} "
                f"PostgreSQL={names['postgres']} rede={names['network']} imagem={names['image']}",
                file=sys.stderr,
            )
        return 1
    finally:
        if not preserve_failed_resources:
            for key, created in (
                ("simulator", simulator_created),
                ("candidate_api_secondary", candidate_api_secondary_created),
                ("candidate_api", candidate_api_created),
                ("api", api_created),
                ("postgres", postgres_created),
            ):
                if created:
                    subprocess.run(("docker", "rm", "--force", names[key]), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            if network_created:
                subprocess.run(("docker", "network", "rm", names["network"]), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            if image_built:
                subprocess.run(("docker", "image", "rm", names["image"]), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        if worktree_added and worktree is not None:
            temp_root = Path(tempfile.gettempdir()).resolve()
            if temp_root not in worktree.resolve().parents:
                print("Worktree fora do temp não foi removido automaticamente.", file=sys.stderr)
            else:
                subprocess.run(("git", "worktree", "remove", "--force", str(worktree)), cwd=repo, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        if temp_context is not None:
            temp_context.cleanup()


if __name__ == "__main__":
    raise SystemExit(main())
