# Redação de logs históricos de pagamento

A API remove dados sensíveis dos logs de pagamento ao gravar novas mensagens e ao devolver registros existentes. Este procedimento, separado, permite limpar valores que possam ter sido persistidos antes dessa proteção.

## Antes de aplicar

- Confirme um backup recente, íntegro e com restauração conhecida; registre o SHA do backup.
- Aguarde todas as instâncias da API executarem a versão que redige mensagens novas.
- Revise as contagens e ensaie o procedimento numa cópia isolada do banco. As consultas deste script não mostram mensagens nem IDs.
- Use o perfil PostgreSQL aprovado; não passe senhas ou URLs de conexão pela linha de comando.

## Preflight

O modo padrão só conta registros e alterações necessárias:

```powershell
psql -X -v ON_ERROR_STOP=1 -v apply=false "service=vendmachines-maintenance" -f tools/Database/Redact-PaymentTelemetrySensitiveMessages.sql
```

Para ensaiar com PostgreSQL descartável e mensagens sintéticas, execute `tools/Database/Test-PostgresPaymentLogRedaction.ps1`. O container não tem rede, portas publicadas ou volumes do host. O ensaio verifica preflight sem exposição de mensagens, modo padrão sem alterações, redação em lotes e preservação de uma mensagem operacional segura.

## Aplicação autorizada

Somente depois de revisar as contagens e ensaiar em clone isolado, execute em janela aprovada com uma conta de manutenção autorizada a atualizar `TransactionTelemetryLogs.Message`:

```powershell
psql -X -v ON_ERROR_STOP=1 -v apply=true "service=vendmachines-maintenance" -f tools/Database/Redact-PaymentTelemetrySensitiveMessages.sql
```

O script mascara atribuições de credenciais, valores `Bearer`, tokens Mercado Pago, JWTs e e-mails. Prefixos de mensagens legadas já conhecidas são substituídos por mensagens genéricas. Atualiza apenas `Message`, em lotes confirmados de até 100 linhas, com `lock_timeout` de um segundo e `SKIP LOCKED`. Linhas bloqueadas podem permanecer para outra execução; o preflight final deve retornar `rows_to_change = 0` antes de encerrar a limpeza.

O script nunca foi aplicado a banco de produção. Esta documentação e o ensaio sintético não substituem backup, revisão das contagens nem aprovação operacional; a limpeza física continua pendente.
