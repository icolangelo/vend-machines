# Tarefas pendentes

## Telemetria e sessões MDB

- [x] Separar o encerramento físico da sessão do processamento financeiro.
  - [x] Persistir `ClosedAt` assim que um `pool.end_session` válido for recebido, antes de iniciar cancelamento, estorno ou processamento da outbox.
  - [x] Garantir que falhas temporárias no Mercado Pago, na outbox ou no banco durante o processamento financeiro não mantenham a sessão visível como aberta na Telemetria.
  - [x] Manter o estado financeiro independente do encerramento físico, usando estados como `CancellationPending`, `RefundPending` e `ReconciliationRequired` quando necessário.
  - [x] Garantir que o processamento financeiro possa continuar depois que a sessão física já estiver fechada.
  - [x] Adicionar uma restrição única no banco que permita somente uma sessão com `ClosedAt == null` por máquina.
  - [x] Tratar de forma segura uma eventual violação da restrição ao tentar abrir duas sessões simultaneamente.
  - [x] Cobrir os cenários com e sem `transactionId`, incluindo falha do provedor e mensagens duplicadas.
