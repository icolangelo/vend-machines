# Tarefas pendentes

O plano completo para lançamento comercial está em [docs/roadmap-producao.md](docs/roadmap-producao.md). Este arquivo mantém o histórico das tarefas de telemetria já concluídas.

## Telemetria e sessões MDB

- [x] Separar o encerramento físico da sessão do processamento financeiro.
  - [x] Persistir `ClosedAt` assim que um `pool.end_session` válido for recebido, antes de iniciar cancelamento, estorno ou processamento da outbox.
  - [x] Garantir que falhas temporárias no Mercado Pago, na outbox ou no banco durante o processamento financeiro não mantenham a sessão visível como aberta na Telemetria.
  - [x] Manter o estado financeiro independente do encerramento físico, usando estados como `CancellationPending`, `RefundPending` e `ReconciliationRequired` quando necessário.
  - [x] Garantir que o processamento financeiro possa continuar depois que a sessão física já estiver fechada.
  - [x] Adicionar uma restrição única no banco que permita somente uma sessão com `ClosedAt == null` por máquina.
  - [x] Tratar de forma segura uma eventual violação da restrição ao tentar abrir duas sessões simultaneamente.
  - [x] Cobrir os cenários com e sem `transactionId`, incluindo falha do provedor e mensagens duplicadas.

## Encerramento gracioso da telemetria

- [x] Bloquear novos comandos de máquina ao iniciar o encerramento da API e aguardar operações em andamento dentro de um prazo limitado.
- [x] Esperar a mensagem MDB já recebida terminar de entrar na fila antes de iniciar o fechamento do WebSocket.
- [x] Esvaziar ACKs e comandos já enfileirados antes de solicitar reconexão; completar o handshake de fechamento do WebSocket.
- [x] Persistir a mensagem na inbox SQL antes do ACK; confirmar e travar a propriedade atual do socket junto à transação da inbox, e usar chave de idempotência que deduplica retransmissões na mesma sessão sem suprimir status ou eventos legítimos após reboot do firmware.
- [x] Processar inbox com quatro workers, ordem por máquina, retomada de `Pending`/`Retry`/`Processing`, renovação condicional do lease durante o processamento, limite de 90 segundos, até oito tentativas e estado terminal `Failed` reprocessável pelo `PlatformAdmin`.
- [x] Limitar pendências (padrão 5.000) de forma atômica entre réplicas PostgreSQL, aplicar o mesmo limite ao reprocessamento administrativo, reter itens concluídos por 90 dias, remover segredos do payload administrativo e registrar auditoria ao reprocessar.
- [x] Exibir contagens e mensagens falhas na aba administrativa de telemetria, com confirmação antes de reprocessar.
- [x] Drenar mensagens duráveis por até 20 segundos no encerramento; o restante permanece no banco para retomada após reinício.
- [ ] Aplicar a migration aditiva `AddDeviceMessageInbox` durante release controlada e comprovar a retomada em homologação. O binário anterior não consome a inbox; rollback precisa preservar as linhas pendentes. O gate de release e a retomada de novas sessões agora bloqueiam enquanto qualquer linha da inbox não estiver `Processed`. Em 26/10/2026, o usuário informou que não há ambiente de homologação, conta Mercado Pago de teste ou máquina MDB de bancada disponível e que fará os ensaios posteriormente; manter pendente até então. Seguir a matriz de [homologação da continuidade MDB](docs/deploy-seguro.md#homologacao-obrigatoria-da-continuidade-mdb).
- [ ] Validar reconexão, ACK/retry e retomada com o firmware MDB real em homologação; não interromper máquinas de produção para esse teste. A inbox agora ajusta `ReceivedAt` sob o lock da máquina para não ordenar uma mensagem nova antes das pendentes quando a reconexão chega a uma instância com relógio atrasado. Respostas `MDB_STATUS` também só liberam a operação aguardando depois que a inbox confirmou a persistência e o ACK foi enfileirado; falha de persistência deixa o pedido sem confirmação e evita avançar com estado não durável. Frames `type: "msg"` sem ID inteiro ou com `data` ausente/não objeto agora são rejeitados sem ACK; o cache de deduplicação compara o hash do payload, então reutilizar um ID com conteúdo diferente não descarta o evento. Em PostgreSQL, a persistência também falha fechada se não receber o ID do nó proprietário para confirmar a rota atual. A matriz de release exige simular a queda do worker depois dos efeitos de domínio e antes de marcar a inbox como `Processed`, conferindo venda/estoque/encerramento, classificação da repetição e ausência de comandos duplicados. Testes com PostgreSQL descartável confirmaram a ordem da inbox com relógio atrasado após reconexão e ambas as ordens de corrida entre a troca de proprietário e a persistência; o reprocessamento de entrega após falha ao concluir a inbox também preservou uma venda e uma baixa em PostgreSQL. WebSocket local confirmou ACK/retransmissão e falha de persistência em SQLite. Esses ensaios sintéticos não comprovam o protocolo nem a retransmissão no firmware físico, que continuam pendentes. O usuário fará essa validação posteriormente, pois não há ambiente de homologação nem máquina de bancada disponíveis agora. Registrar limites e critérios da mesma [matriz de homologação](docs/deploy-seguro.md#homologacao-obrigatoria-da-continuidade-mdb).
