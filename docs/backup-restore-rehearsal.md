# Backup e ensaio de restauração PostgreSQL

Este procedimento produz um dump customizado de um único banco e verifica a restauração em um container Docker local e isolado, sem rede, portas publicadas ou volumes do host. O verificador não recebe uma connection string, não conecta ao servidor de origem e não altera o banco de origem.

## Gerar o backup

1. Confirme com o provedor e com a operação a janela, o banco de origem, o ponto de recuperação e o destino protegido do arquivo. Escolha uma imagem `postgres` com a versão principal adequada ao servidor de origem.
2. Obtenha `PGPASSWORD` do secret manager na sessão local. Não o inclua no comando, no histórico do terminal, em arquivos do repositório ou em logs.
3. Defina os dados não secretos da conexão e um diretório de backup com acesso restrito:

```powershell
# Configure PGPASSWORD na sessão por meio do secret manager aprovado.
if ([string]::IsNullOrWhiteSpace($env:PGPASSWORD)) { throw "PGPASSWORD não está definido nesta sessão." }
$DbHost = "host-fornecido-pelo-provedor"
$DbName = "nome-do-banco"
$DbUser = "usuario-de-backup"
$PostgresImage = "postgres:17-alpine" # Ajuste para a versão compatível com a origem.
$BackupDirectory = "D:\backups\vend-machines"
$BackupName = "vendmachines-$(Get-Date -Format 'yyyyMMdd-HHmmss').dump"
New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null

docker run --rm `
  --mount "type=bind,source=$BackupDirectory,target=/backup" `
  --env PGPASSWORD `
  $PostgresImage `
  pg_dump --host=$DbHost --username=$DbUser --dbname=$DbName `
    --sslmode=require --format=custom --no-owner --no-acl `
    --file="/backup/$BackupName"
if ($LASTEXITCODE -ne 0) { throw "pg_dump falhou com código $LASTEXITCODE." }
$BackupFile = Join-Path $BackupDirectory $BackupName
if (-not (Test-Path -LiteralPath $BackupFile) -or (Get-Item -LiteralPath $BackupFile).Length -le 0) {
  throw "O dump não foi criado ou está vazio."
}
Get-FileHash -Algorithm SHA256 -LiteralPath $BackupFile
```

4. O exemplo interrompe o procedimento se `pg_dump` falhar ou o arquivo estiver ausente/vazio e calcula SHA-256. Guarde checksum, horário UTC, SHA/release, tamanho e local do backup no registro restrito de operação. Aplique a política aprovada de criptografia, retenção e cópias externas.

## Restaurar em ambiente isolado

Use a versão principal PostgreSQL compatível com o backup. O script cria um container local com nome aleatório e rede desativada, restaura em um banco vazio, confirma `__EFMigrationsHistory` e `Users`, informa apenas a quantidade de migrations e remove o container ao terminar.

```powershell
.\tools\Database\Verify-PostgresBackup.ps1 `
  -BackupPath (Join-Path $BackupDirectory $BackupName) `
  -PostgresImage $PostgresImage
```

O script aceita dumps no formato customizado do `pg_dump`. Requer Docker em execução e espaço local suficiente para o banco restaurado. Código diferente de zero significa falha no ensaio; preserve o backup e investigue antes de considerar recuperação validada.

## Critério de aceite e limite

- O backup pode ser listado e restaurado sem erro pelo `pg_restore`.
- As tabelas `__EFMigrationsHistory` e `Users` existem e há pelo menos uma migration registrada.
- A restauração ocorre em instância isolada, sem sobrescrever o servidor de origem.
- Registre duração e tamanho do dump/restauração, compare a versão de schema e valide consultas funcionais em uma cópia restrita.

Em 27/09/2026, o script foi exercitado com dump PostgreSQL 17 contendo schema e dados sintéticos; o container de restauração concluiu e foi removido. Isso valida o utilitário, mas não confirma backup do provedor real, volume/tempo de recuperação, acesso, retenção, criptografia, RPO/RTO nem restauração completa dos dados de produção. Preencha provedor, responsáveis, retenção, RPO/RTO e referência do backup antes de fechar a etapa da Fase 5. Não restaure sobre produção como parte deste ensaio.

Na revalidação de 27/09/2026, o fixture inicial encontrou uma corrida: `pg_isready` pode responder durante o servidor temporário de bootstrap do entrypoint, que é desligado antes do PostgreSQL principal iniciar. Os dois loops agora exigem três respostas consecutivas de prontidão. `Test-PostgresBackupRestore.ps1 -PostgresImage postgres:17-alpine` passou: criou o dump sintético, restaurou-o no container isolado, confirmou o histórico com uma migration e removeu o container e o dump temporário. Ainda é necessário ensaiar um backup recente do provedor e registrar volume, duração, RPO e RTO.

Reexecução em 28/09/2026: `Test-PostgresBackupRestore.ps1 -PostgresImage postgres:17-alpine` concluiu com código zero e confirmou uma migration restaurada. O ensaio foi reforçado para desativar a rede do container tanto no fixture quanto na restauração; não restaram containers do ensaio e o processo local da API continuou ativo. O fixture usa dados sintéticos; o backup do provedor e a restauração de uma cópia recente continuam pendentes.
