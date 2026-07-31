# Tarefas pendentes

## Telemetria e sessões MDB

- [ ] Separar o encerramento físico da sessão do processamento financeiro.
  - Persistir `ClosedAt` assim que um `pool.end_session` válido for recebido, antes de iniciar cancelamento, estorno ou processamento da outbox.
  - Garantir que falhas temporárias no Mercado Pago, na outbox ou no banco durante o processamento financeiro não mantenham a sessão visível como aberta na Telemetria.
  - Manter o estado financeiro independente do encerramento físico, usando estados como `RefundPending` e `ReconciliationRequired` quando necessário.
  - Garantir que o processamento financeiro possa continuar depois que a sessão física já estiver fechada.
  - Adicionar uma restrição única no banco que permita somente uma sessão com `ClosedAt == null` por máquina.
  - Tratar de forma segura uma eventual violação da restrição ao tentar abrir duas sessões simultaneamente.
  - Cobrir os cenários com e sem `transactionId`, incluindo falha do provedor e mensagens duplicadas.

