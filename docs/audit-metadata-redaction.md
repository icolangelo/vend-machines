# Redação de metadados históricos de auditoria

O serviço agora redige campos sensíveis antes de gravar novos metadados e novamente ao devolver registros antigos pela API. Este procedimento remove os valores correspondentes do banco, inclusive se uma auditoria antiga tiver guardado um campo sensível.

## Condições antes de aplicar

- Confirme um backup recente, íntegro e com restauração conhecida; registre o SHA do backup.
- Aguarde todas as instâncias da API executarem a versão que redige metadados novos.
- Revise e ensaie o script numa cópia isolada do banco. A consulta de contagem não revela conteúdo nem IDs.
- Use o perfil PostgreSQL aprovado; não passe senhas ou URLs de conexão pela linha de comando.

## Preflight

O padrão é apenas contagem. Configure um serviço no `pg_service.conf` autorizado e execute:

```powershell
psql -X -v ON_ERROR_STOP=1 -v apply=false "service=vendmachines-maintenance" -f tools/Database/Redact-AuditLogSensitiveMetadata.sql
```

O resultado informa a quantidade de registros com metadados, JSON inválido, valores escalares e registros que precisam de alteração. Não imprime metadados, chaves, IDs, IPs ou dados de clientes. JSON inválido ou escalar será removido do campo `MetadataJson` se a aplicação for autorizada.

Para verificar o preflight e a aplicação em PostgreSQL isolado com dados sintéticos, execute `tools/Database/Test-PostgresAuditMetadataRedaction.ps1`. O teste usa container sem rede, portas publicadas ou volumes do host e confirma que o modo padrão não altera linhas, que os valores sensíveis são removidos e que metadados seguros permanecem intactos.

## Aplicação autorizada

Somente após revisar as contagens e validar a rotina em clone isolado, execute durante a janela aprovada com uma conta de manutenção que possa atualizar `AuditLogs.MetadataJson`:

```powershell
psql -X -v ON_ERROR_STOP=1 -v apply=true "service=vendmachines-maintenance" -f tools/Database/Redact-AuditLogSensitiveMetadata.sql
```

O script redige objetos e arrays JSON em lotes de até 100 linhas, com `lock_timeout` de um segundo e `SKIP LOCKED`; cada lote bem-sucedido é confirmado separadamente. Ele mascara propriedades cujo nome indica senha, segredo, token, credencial, autorização, cookie/cabeçalho, payload, chave, assinatura, e-mail, telefone ou documento. Metadados malformados ou escalares são definidos como `NULL`. O preflight final deve retornar `rows_to_change = 0`; se linhas bloqueadas restarem, aguarde a liberação e repita.

Não use este script para remover registros de auditoria inteiros. A aplicação altera somente `MetadataJson` e não reescreve outras colunas. A operação nunca foi executada em banco de produção; a limpeza física só pode ser considerada concluída após backup, aprovação, aplicação controlada e preflight final com zero pendências.
