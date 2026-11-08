# Backup e ensaio de restauração PostgreSQL

Este procedimento produz um dump customizado de um único banco e verifica a restauração em um container Docker local e isolado, sem rede, portas publicadas ou volumes do host. O verificador não recebe uma connection string, não conecta ao servidor de origem e não altera o banco de origem.

## Gerar o backup

1. Confirme com o provedor e com a operação a janela, o banco de origem, o ponto de recuperação e o destino protegido do arquivo. Escolha uma imagem `postgres` com a versão principal adequada ao servidor de origem.
2. Obtenha `PGPASSWORD` do secret manager na sessão local. Não o inclua no comando, no histórico do terminal, em arquivos do repositório ou em logs.
3. Registre a última migration aplicada no banco de origem usando uma consulta de leitura na janela autorizada; guarde o identificador junto ao checksum e à versão/release esperada:

```sql
SELECT "MigrationId"
FROM "__EFMigrationsHistory"
ORDER BY "MigrationId" DESC
LIMIT 1;
```

Use o resultado literal da origem no horário do backup em `$ExpectedLatestMigrationId`. Não informe a migration candidata da nova versão se ela ainda não estiver aplicada na origem.

4. Defina os dados não secretos da conexão e um diretório de backup com acesso restrito:

```powershell
# Configure PGPASSWORD na sessão por meio do secret manager aprovado.
if ([string]::IsNullOrWhiteSpace($env:PGPASSWORD)) { throw "PGPASSWORD não está definido nesta sessão." }
$DbHost = "host-fornecido-pelo-provedor"
$DbName = "nome-do-banco"
$DbUser = "usuario-de-backup"
$PostgresImage = "postgres:17-alpine" # Ajuste para a versão compatível com a origem.
$BackupDirectory = "D:\backups\vend-machines"
$BackupTimestampUtc = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss'Z'")
$BackupName = "vendmachines-$BackupTimestampUtc.dump"
$BackupFile = Join-Path $BackupDirectory $BackupName
$BackupPartialName = "$BackupName.partial"
$BackupPartialFile = Join-Path $BackupDirectory $BackupPartialName
New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
if ((Test-Path -LiteralPath $BackupFile) -or (Test-Path -LiteralPath $BackupPartialFile)) {
  throw "O destino do backup já existe; escolha outro nome para não sobrescrever um arquivo."
}

docker run --rm `
  --mount "type=bind,source=$BackupDirectory,target=/backup" `
  --env PGPASSWORD `
  --env PGSSLMODE=require `
  $PostgresImage `
  pg_dump --host=$DbHost --username=$DbUser --dbname=$DbName `
    --format=custom --no-owner --no-acl --lock-wait-timeout=5s `
    --file="/backup/$BackupPartialName"
if ($LASTEXITCODE -ne 0) {
  if (Test-Path -LiteralPath $BackupPartialFile) { Remove-Item -LiteralPath $BackupPartialFile -Force }
  throw "pg_dump falhou com código $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $BackupPartialFile) -or (Get-Item -LiteralPath $BackupPartialFile).Length -le 0) {
  if (Test-Path -LiteralPath $BackupPartialFile) { Remove-Item -LiteralPath $BackupPartialFile -Force }
  throw "O dump não foi criado ou está vazio."
}
$BackupSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $BackupPartialFile).Hash
if (Test-Path -LiteralPath $BackupFile) {
  throw "O destino do backup apareceu durante a geração; o arquivo parcial foi preservado para inspeção."
}
Move-Item -LiteralPath $BackupPartialFile -Destination $BackupFile -ErrorAction Stop
$BackupSha256
```

5. O exemplo exige TLS por `PGSSLMODE=require` (variável libpq), limita a cinco segundos a espera para adquirir os locks iniciais e usa o formato customizado sem jobs paralelos, grava primeiro com sufixo `.partial`, recusa sobrescrever destinos existentes e só publica o nome final depois de `pg_dump` terminar e o arquivo ser validado. O timeout evita espera indefinida caso uma tabela esteja sob lock incompatível; não limita o tempo total nem o I/O de leitura, portanto confirme uma janela de baixa carga. Se `pg_dump` falhar ou criar arquivo ausente/vazio, remove o parcial; erro durante checksum ou promoção pode deixá-lo no diretório. Uma interrupção abrupta também pode deixar um `.partial`, que nunca deve ser tratado como backup aprovado. O SHA-256 é calculado antes da promoção do arquivo. Guarde checksum, migration aplicada, horário UTC, SHA/release, tamanho e local do backup no registro restrito de operação. Aplique a política aprovada de criptografia, retenção e cópias externas.

## Restaurar em ambiente isolado

Use a versão principal PostgreSQL compatível com o backup. Compare o SHA-256 e a última migration com os valores registrados no procedimento de geração. O script exige ambos antes de considerar o ensaio aprovado, cria um container local com nome aleatório e rede desativada, restaura em um banco vazio e confirma `__EFMigrationsHistory`, `Users` e a versão do schema. No encerramento, verifica se o container existe, remove-o à força se necessário e confirma a remoção; se Docker estiver indisponível ou a remoção falhar, termina com erro explícito.

```powershell
.\tools\Database\Verify-PostgresBackup.ps1 `
  -BackupPath (Join-Path $BackupDirectory $BackupName) `
  -ExpectedSha256 $BackupSha256 `
  -ExpectedLatestMigrationId $ExpectedLatestMigrationId `
  -PostgresImage $PostgresImage
```

O script aceita dumps no formato customizado do `pg_dump`. Ele compara a migration restaurada com a capturada na origem; informe o identificador exato registrado para este backup. Requer Docker em execução e espaço local suficiente para o banco restaurado. Código diferente de zero significa falha no ensaio; preserve o backup e investigue antes de considerar recuperação validada.

## Ensaiar a cadeia de migrations numa cópia

Quando houver um backup recente e o pacote canônico da release candidata, use o mesmo arquivo de backup e os valores registrados na geração. Este ensaio cria outro container PostgreSQL com `--network none`, sem portas publicadas nem volumes do host; restaura o dump, confere a migration base, valida todos os checksums do pacote e aplica os sete segmentos na ordem do runbook. Ele ignora cada segmento concorrente somente quando a migration já está registrada e o índice correspondente está válido. Ao final, confere a versão candidata e os índices. O cleanup verifica se o container existe, remove-o à força se necessário e confirma a remoção; indisponibilidade do Docker ou falha na remoção encerra o processo com erro explícito. Não recebe connection string nem acessa o servidor de origem.

```powershell
$BackupFile = "D:\backups\vend-machines\backup-recente.dump"
$PackageDirectory = "D:\releases\release-package-SHA"
$RehearsalScript = Join-Path $PackageDirectory "Rehearse-PostgresMigrations.ps1"
$BackupSha256 = (Get-FileHash -LiteralPath $BackupFile -Algorithm SHA256).Hash
$ExpectedBackupLatestMigrationId = "migration registrada junto ao backup"
$ExpectedReleaseSha = "SHA completo aprovado da release"
$ExpectedCandidateLatestMigrationId = (Select-String `
  -LiteralPath (Join-Path $PackageDirectory "postgresql-migration-inventory-mapping-index.sql") `
  -Pattern "VALUES \('(\d{14}_[A-Za-z0-9_]+)'" |
  ForEach-Object { $_.Matches[0].Groups[1].Value } |
  Select-Object -Last 1)

& $RehearsalScript `
  -BackupPath $BackupFile `
  -ExpectedBackupSha256 $BackupSha256 `
  -ExpectedBackupLatestMigrationId $ExpectedBackupLatestMigrationId `
  -ReleasePackagePath $PackageDirectory `
  -ExpectedReleaseSha $ExpectedReleaseSha `
  -ExpectedCandidateLatestMigrationId $ExpectedCandidateLatestMigrationId `
  -PostgresImage "postgres:17-alpine"
```

Substitua os textos de exemplo pelo identificador exato registrado no backup e pelo SHA conhecido da release aprovada; não derive o SHA esperado do próprio pacote. A extração acima obtém a última migration do segmento final e deve produzir um ID não vazio antes de continuar. Use a versão principal PostgreSQL compatível com a origem. Registre duração de cada segmento, duração total, tamanho do backup e resultado. Se qualquer verificação ou segmento falhar, o processo termina com erro e remove o container; investigue o estado do ensaio antes de repetir, em especial um possível índice inválido de uma tentativa interrompida. O ensaio mede a execução sobre a cópia restaurada, mas não altera a origem nem substitui homologação funcional.

`Test-PostgresMigrationRehearsal.ps1 -PostgresImage postgres:17-alpine` monta dumps e um pacote sintéticos, executa os sete segmentos em containers isolados e cobre três estados: migrations pendentes, migrations/índices já aplicados e migration registrada sem o índice exigido. O último caso precisa falhar fechado. Também confirma que um segmento alterado é rejeitado antes de iniciar a restauração. O teste roda nos workflows de verificação e de release. Ele verifica a ferramenta e os gates de integridade; não substitui o ensaio com backup e migrations da release real.

## Critério de aceite e limite

- O backup pode ser listado e restaurado sem erro pelo `pg_restore`.
- O SHA-256 do arquivo corresponde ao valor registrado no momento da geração.
- A última migration restaurada corresponde ao identificador capturado da origem no momento do backup.
- As tabelas `__EFMigrationsHistory` e `Users` existem e há pelo menos uma migration registrada.
- A restauração ocorre em instância isolada, sem sobrescrever o servidor de origem.
- Registre duração e tamanho do dump/restauração, compare a versão de schema e valide consultas funcionais em uma cópia restrita.
- A sequência candidata de migrations é aplicada em outra cópia isolada, termina na versão esperada e deixa válidos os dois índices concorrentes; registre duração e resultado de cada segmento.

Em 27/09/2026, o script foi exercitado com dump PostgreSQL 17 contendo schema e dados sintéticos; o container de restauração concluiu e foi removido. Isso valida o utilitário, mas não confirma backup do provedor real, volume/tempo de recuperação, acesso, retenção, criptografia, RPO/RTO nem restauração completa dos dados de produção. Preencha provedor, responsáveis, retenção, RPO/RTO e referência do backup antes de fechar a etapa da Fase 5. Não restaure sobre produção como parte deste ensaio.

Na revalidação de 27/09/2026, o fixture inicial encontrou uma corrida: `pg_isready` pode responder durante o servidor temporário de bootstrap do entrypoint, que é desligado antes do PostgreSQL principal iniciar. Os dois loops agora exigem três respostas consecutivas de prontidão. `Test-PostgresBackupRestore.ps1 -PostgresImage postgres:17-alpine` passou: criou o dump sintético, restaurou-o no container isolado, confirmou o histórico com uma migration e removeu o container e o dump temporário. Ainda é necessário ensaiar um backup recente do provedor e registrar volume, duração, RPO e RTO.

Reexecução em 28/09/2026: `Test-PostgresBackupRestore.ps1 -PostgresImage postgres:17-alpine` concluiu com código zero e confirmou uma migration restaurada. O ensaio foi reforçado para desativar a rede do container tanto no fixture quanto na restauração; não restaram containers do ensaio e o processo local da API continuou ativo. O fixture usa dados sintéticos; o backup do provedor e a restauração de uma cópia recente continuam pendentes.

Revalidação em 24/10/2026: `Test-PostgresBackupRestore.ps1 -PostgresImage postgres:17-alpine` concluiu com código zero e confirmou novamente uma migration em uma restauração PostgreSQL isolada. O teste criou e removeu o dump e os containers temporários. O resultado não inclui dados nem backup do provedor; ainda faltam cópia recente, medição de RPO/RTO, retenção e validação operacional.

Revalidação em 25/10/2026: `Test-PostgresBackupRestore.ps1 -PostgresImage postgres:17` concluiu com código zero; restaurou o dump sintético e encontrou uma migration, depois rejeitou um SHA-256 incorreto antes de iniciar outro container. Fixture e restauração usaram containers com rede desativada; confirmei que foram removidos e que o PostgreSQL local preexistente permaneceu ativo. Continua pendente o ensaio com backup recente do provedor, medição de duração/tamanho, RPO/RTO, retenção e validação funcional do banco restaurado.

Revalidação em 25/10/2026: `Test-PostgresMigrationRehearsal.ps1 -PostgresImage postgres:17-alpine` passou em PostgreSQL 17 descartável. O harness restaurou um dump sintético com migrations pendentes e aplicou os cinco segmentos; restaurou outro que já continha as duas migrations e índices válidos e confirmou que os segmentos concorrentes foram ignorados; depois restaurou um backup inconsistente (migration de sessão registrada sem índice) e comprovou a falha fechada e limpeza do container. Por fim, adulterou um segmento e comprovou que o checksum o rejeitou antes da restauração. O cleanup valida que o diretório temporário resolvido é filho direto do temp configurado antes de removê-lo recursivamente. Containers, dumps e diretório temporário foram removidos; `pgvector-db`, que já estava ativo, permaneceu intacto. O gate do workflow passou 17/17 e o parser PowerShell não encontrou erros. Ainda falta executar `Rehearse-PostgresMigrations.ps1` com backup recente e pacote da release aprovada, medir locks/I/O/RPO/RTO e validar consultas funcionais na cópia.

Revalidação em 26/10/2026: `Test-PostgresBackupRestore.ps1 -PostgresImage postgres:17-alpine` terminou com código zero. O dump fixture foi restaurado em outro container com rede desativada; o verificador confirmou a migration esperada e recusou tanto checksum divergente quanto versão de schema incorreta. O script removeu o dump e os containers descartáveis. O PostgreSQL de produção/provedor não foi consultado; o ensaio com backup recente, consultas funcionais e medição de RPO/RTO continua pendente.

## Verificação de integridade do arquivo antes da restauração

O verificador exige `-ExpectedSha256` e compara o arquivo com o checksum registrado antes de criar o container. Se o arquivo tiver sido trocado ou alterado desde a geração, a restauração é recusada. Depois de restaurar, exige a última migration aplicada registrada para aquele backup. O teste sintético rejeita checksum e versão de migration incorretos. O checksum comprova identidade do arquivo em relação ao registro; mantenha o registro separado e protegido junto dos metadados do backup.
