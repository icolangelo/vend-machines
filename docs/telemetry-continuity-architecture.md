# Arquitetura para atualizar a API sem interromper máquinas conectadas

## Objetivo

Atualizar a API mantendo telemetria, comandos, sessões e operações financeiras das máquinas já conectadas. Fechar um WebSocket e aguardar reconexão não atende a este objetivo, ainda que o intervalo seja curto.

## Estado técnico confirmado

- `TelemetryConnectionManager` mantém cada WebSocket e a fila local de envio na memória do processo da API. O shutdown fecha os sockets com `EndpointUnavailable`.
- A API candidata registra proprietário, conexão, versão de roteamento e lease. Instâncias candidatas encaminham comandos e consultas MDB pela fila persistida no PostgreSQL.
- `SessionOrchestrator`, `MdbStatusRequestService`, `MachinesController` e `TelemetryController` agora dependem de `ITelemetryConnectionOperations`, registrado para a mesma instância singleton que hospeda os sockets. Isso cria um ponto de substituição nas regras de negócio, sem trocar o transporte atual.
- A versão atual de produção em `develop` não registra o proprietário nem consome a fila de dispatch. Na sobreposição, a candidata não pode enviar pelo dispatch novo para um socket legado; o relay HTTP direciona operações interativas à instância antiga que mantém esse socket.
- A inbox durável reduz perda de mensagens já gravadas, mas não transfere um socket TCP/WebSocket entre processos.

Existe um relay HTTP privado (`Telemetry:LegacyApiBaseUrl`) para início de sessão, consulta manual MDB, cancelamento, comando manual e webhook de pagamento direcionados a sockets que a API anterior ainda possui. Ele preserva o bearer do usuário e não segue redirects nem repete automaticamente a chamada. Para comando manual, a candidata grava uma intenção com `RequestId` antes do forward e nunca retransmite uma intenção cujo resultado ficou incerto. O readiness consulta `/health` da API publicada em `origin/develop`; considera saudáveis respostas `Healthy` ou `Ready`. Sockets legados recentes só são aceitos como roteáveis quando têm `ConnectionId`, a origem privada está configurada e a API anterior responde com sucesso.

O probe de saúde e cada chamada encaminhada levam `X-VendMachines-Legacy-Relay-Hop: 1`. A API anterior ignora esse cabeçalho; se a origem configurada retornar à própria candidata, `LegacyRelayLoopGuardMiddleware` responde `508 legacy_relay_loop_blocked` antes de executar qualquer endpoint. Assim uma origem pública ou auto-referenciada não pode produzir relay recursivo, e readiness não aceita a candidata como owner antigo.

Durante a coexistência, os workers candidatos deixam ao processo anterior o polling MDB, deadlines de sessões e reconciliação de pagamentos associados a uma sessão ativa num socket legado. O worker de deadlines da candidata filtra sockets recentes sem owner/lease roteável antes de buscar sessões; um timeout de pagamento legado não deve criar cancelamento, outbox ou comando na candidata. O binário de `origin/develop` continua responsável por essas sessões até a migração do socket. Webhooks desses pagamentos são encaminhados, com query e cabeçalhos de verificação preservados, para a API antiga antes de a candidata confirmar o recebimento; sem relay configurado ou em caso de erro, a candidata responde com falha para permitir nova tentativa do provedor. Assim o evento chega ao processo que ainda pode comandar a máquina, em vez de ficar preso numa inbox que o binário antigo não lê.

O worker antigo de `OutboxMessages` consome apenas linhas `pending` e não as reivindica atomicamente. Durante a sobreposição, operações financeiras de sessões processadas pela candidata são gravadas como `deferred_candidate_overlap`, estado ignorado pelo binário antigo. A candidata agora reivindica e processa somente essas linhas, com transição atômica para `processing` e lease, enquanto deixa as linhas `pending` antigas para o worker legado. Tentativas com falha retornam ao estado ignorado pelo binário antigo e preservam o backoff. Ao retirar a API antiga e remover essa configuração, os itens restantes são promovidos para processamento normal. Isso mantém estornos/cancelamentos das sessões candidatas ativos durante a coexistência sem deixar os dois binários processarem a mesma linha.

O relay não oferece exatamente uma execução no servidor antigo: o binário de `origin/develop` ignora o `RequestId` e grava sua própria auditoria de comando; a proteção contra retransmissão existe somente na candidata e somente para repetição do mesmo identificador. Inícios de sessão, consulta MDB e cancelamentos ainda não têm ledger idempotente compartilhado. Polling, deadlines e reconciliação dependem de a API anterior continuar saudável e executar seus workers. Mensagens recebidas são processadas pelo próprio processo que possui o socket. O comportamento de revogação de credenciais, alteração de configuração e falha/retorno abrupto da API anterior ainda exige ensaio de sobreposição; o relay não é evidência de operação contínua com firmware físico.

Para cancelar uma sessão, a candidata retorna `202 Accepted` sem relay quando o estado compartilhado já é `ClosingWithoutSelection` ou a sessão já está fechada. Chamadas concorrentes roteadas pela candidata são serializadas entre réplicas pelo lock PostgreSQL da máquina, conforme o ensaio descrito abaixo. Esse lock não cobre chamadas diretas à API antiga e não torna recuperável uma falha entre a gravação do estado e o envio do comando antigo; esses casos ainda exigem um protocolo de idempotência/reconciliação comum às versões.

Para início pelo painel, se a API antiga responder `session_already_active`, a candidata localiza uma sessão `panel` ainda aberta da mesma máquina, empresa e usuário e retorna o registro/ID existente; se pertencer a outro usuário, preserva o conflito. Isso recupera retries enquanto a sessão continua ativa e evita um segundo `ABRIR_SESSAO`. Um ensaio PostgreSQL side-by-side com duas candidatas, atraso sintético antes da inserção e quatro inícios simultâneos confirmou que todas retornam o mesmo ID e gravam uma única sessão/comando. Não cobre retries após o fechamento nem cria uma chave idempotente durável; chamadas diretas à API antiga ou várias réplicas antigas não compartilham essa coordenação.

Cancelamentos roteados ao processo antigo são serializados entre réplicas candidatas pelo lock PostgreSQL da máquina. A candidata abre transação, adquire `MachineOperationLock`, relê o estado da sessão e mantém o lock durante o relay ao owner antigo; depois de obter o lock, conclui essa chamada sem depender do navegador permanecer conectado, limitada pelo timeout HTTP de 20 segundos. As requisições concorrentes seguintes reavaliam o estado e não encaminham outro `FECHAR_SESSAO`. O ensaio com duas candidatas, PostgreSQL compartilhado, atraso sintético no owner e quatro requests simultâneas confirmou um único registro `FECHAR_SESSAO`. O lock só coordena chamadas que passam pela candidata; acesso direto ao endpoint antigo o contorna. Um resultado incerto após o timeout ainda precisa de reconciliação, pois a API antiga não consome um ID idempotente compartilhado.

Rotação e revogação de credencial não são encaminhadas à API anterior. Antes de alterar o segredo compartilhado, a candidata verifica se uma máquina online tem rota de desconexão confirmada; sem owner roteável, retorna `503 machine_owner_unknown` antes de gravar, preservando a conexão e a credencial ativa. A operação continua disponível para máquina offline ou owner atual roteável. Essa ação administrativa fica temporariamente indisponível para sockets legados até existir um protocolo idempotente de desconexão/rotação entre versões.

O relay não move o endpoint `/telemetria` para outro processo nem muda o shutdown. A continuidade só estará implementada quando todos os fluxos tiverem dono único, autorização preservada, idempotência compatível entre os binários, reconciliação de resultado incerto e aprovação na matriz de aceitação.

Logo, subir uma segunda instância, por si só, não prova continuidade. A instância antiga precisa permanecer viva enquanto tiver sockets ativos e as operações direcionadas a esses sockets precisam continuar chegando ao proprietário correto.

## Readiness sem pausar a operação

O workflow e `/health/deploy-ready` já não exigem `active_vend_sessions_drained=yes` nem `legacy_manual_commands_paused=yes`. A admissão precisa permanecer aberta. Sessões ativas são aceitas somente quando cada máquina tem conexão fresca, owner, versão de roteamento e lease válidos; sessões sem rota bloqueiam com `active_session_without_routable_machine`, e qualquer socket online legado sem rota bloqueia com `machine_connections_unroutable`.

O trigger PostgreSQL da admissão continua disponível para uma manutenção excepcional que aceite suspender novas vendas, mas não é um mecanismo de release sem impacto. Para a continuidade integral, as migrations precisam ser compatíveis com os dois binários, e cada operação financeira ou MDB precisa de dono único, idempotência e reconciliação. Não marque continuidade se novas vendas/comandos forem pausados, se um socket for fechado/reconectado ou se uma operação não puder alcançar o dono da conexão.

## Caminho de atualização

### Pré-condição de infraestrutura

O ambiente precisa permitir que a versão antiga e a candidata executem simultaneamente, com endpoints internos distintos, e que novas conexões sejam encaminhadas à candidata sem encerrar conexões existentes na antiga. A candidata também precisa alcançar a instância antiga por uma rota privada autenticada. Se o ambiente só puder substituir um container por vez, a troca interromperá os sockets mantidos pela API atual e não atenderá ao requisito.

### Ponte de compatibilidade

Antes de encaminhar operações de máquinas legadas para a candidata, implementar uma ponte de compatibilidade para sockets ainda pertencentes à versão antiga. A ponte deve identificar o dono da conexão, encaminhar ao processo antigo as operações suportadas e preservar autenticação, escopo da empresa, idempotência e auditoria. Operações que possam criar venda, encerrar sessão, alterar estoque ou despachar comando precisam ter um único executor e um resultado reconciliável. Não basta repetir a chamada pública com outro token ou duplicar o processamento nos dois binários.

A fila PostgreSQL existente continua sendo o caminho entre instâncias com roteamento compatível. A ponte atende somente conexões legadas durante a janela de sobreposição e deve ser removida quando não houver mais sockets legados nem operações pendentes atribuídas a eles.

### Operações que a ponte deve cobrir

O levantamento do código mostra que a ponte não pode encaminhar somente comandos manuais:

- comando manual autenticado em `TelemetryController.SendCommand`, incluindo o registro único de intenção, ACK, tentativas e resultado incerto;
- consulta `MDB_STATUS` solicitada pelo painel e pelo `MdbStatusPollingWorker`, com correlação da resposta ao pedido;
- comandos iniciados pelo `SessionOrchestrator` para abertura, conclusão, falha e fechamento físico de sessão, inclusive após eventos de pagamento;
- desconexão explícita por revogação ou troca de credencial, além das operações de configuração que alteram o estado da máquina;
- mensagens e ACKs recebidos no WebSocket antigo enquanto a candidata também processa webhooks, sessão, venda e estoque no mesmo banco.

Para cada fluxo, deve haver um único dono da execução e uma regra de idempotência compatível entre os dois binários. Particularmente, não se pode deixar a candidata aplicar venda/estoque enquanto o binário antigo também processa o mesmo evento, nem retransmitir um comando cujo ACK tenha se perdido. Conexões WebSocket do painel podem precisar reconectar separadamente; isso não substitui nem pode mascarar a métrica de continuidade dos sockets MDB.

### Sequência de rollout

1. Aplicar e revisar somente migrations aditivas compatíveis com o binário antigo; confirmar backup restaurável e histórico do schema.
2. Iniciar a candidata em paralelo sem parar a API antiga. Manter tráfego de máquina e operações do usuário no caminho compatível até a ponte provar a propriedade e autorização corretas.
3. Encaminhar novas conexões de máquina à candidata. Manter a antiga ativa para conexões existentes e verificar, por máquina, proprietário, versão de roteamento, heartbeat e filas pendentes.
4. Direcionar cada comando, consulta MDB, resultado de pagamento, sessão e evento de telemetria ao proprietário efetivo. Falhar fechado quando a propriedade for ambígua; nunca enviar em ambos os nós.
5. Manter a API antiga viva enquanto houver socket, sessão, comando ou mensagem atribuída a ela. Encerrá-la somente quando todos esses contadores forem zero e os testes de operação contínua tiverem passado.
6. Reverter mantendo o schema expandido e as filas persistidas. Não encerrar a candidata antes de confirmar que nenhum socket ou operação depende dela.

## Critérios de aceitação

- Firmware mais antigo suportado e atual permanecem conectados durante a sobreposição e a retirada do nó antigo; nenhum `EndpointUnavailable`, reconexão forçada ou lacuna de heartbeat durante a troca.
- Telemetria, comando manual, status MDB, início/fechamento de sessão, aprovação/recusa de pagamento, estoque e eventos do painel continuam com um único efeito e dentro dos limites operacionais definidos.
- Testes exercitam simultaneamente binário antigo e candidato, PostgreSQL compartilhado, perda do ACK, retry, mudança de proprietário, rollback e encerramento de cada nó.
- Ensaio físico confirma esses critérios com a matriz de firmware suportada. Simulador e testes locais são necessários, mas não suficientes para liberar o gate de produção.

## Capacidade de infraestrutura confirmada; integração pendente

O responsável confirmou que o servidor consegue manter a API antiga e a candidata em paralelo, com acesso interno à versão antiga durante a migração. Isso remove a dúvida sobre coexistência como capacidade de infraestrutura, mas ainda não comprova que a configuração do relay, a rede privada, as regras de encaminhamento e os fluxos da frota estejam corretos. `machine_connections_preserved` permanece `no` até executar e revisar essa validação com sockets ativos e o firmware suportado; simulador e ensaios locais não substituem essa evidência.

Adicionei [preflight somente leitura da sobreposição](partial-production-rollout.md) e `tools/Deployment/Validate-TelemetryOverlapReadiness.py`. O script consulta readiness, SHA da candidata, conexões legadas, saúde do relay e conexões sem rota; exige o mínimo de conexões informado pela pessoa operadora conforme a contagem esperada da frota. Ele não envia comandos, não altera estado e não segue redirects. Os testes locais cobrem sucesso, ausência de sockets, SHA divergente, argumento de contagem ausente e rejeição de redirects (**6/6**). A execução real ainda depende de acesso autorizado à rede do servidor e de um token administrativo temporário, que não deve ser compartilhado.

## Evidência local dos fluxos encaminhados

O ensaio descartável com PostgreSQL, binário `origin/develop`, candidata e simulador agora atravessa a API candidata para iniciar sessão, consultar status MDB com `messageId` correlacionado, cancelar a sessão e aguardar `ClosedWithoutSelection` confirmado pelo evento MDB. O mesmo ensaio cobre comando manual com ACK, webhook de pagamento roteado à API antiga sem inbox duplicada, outbox financeira adiada e preservação do socket durante o startup da candidata. Acrescentei a parada forçada e reinicialização do container candidato enquanto a API anterior continua dona do WebSocket: antes, durante e depois, o `ConnectionId` legado permaneceu online, o simulador continuou recebendo pings e um comando via relay recebeu ACK após o restart. Em seguida, ao encerrar abruptamente a API antiga mantendo sua presença sintética recente, readiness bloqueia com `legacy_owner_api_unavailable`; um novo comando termina como `command_outcome_unknown` e não cria dispatch na candidata. A execução completa terminou com os marcadores `legacy_socket_survives_candidate_stop=yes`, `legacy_socket_survives_candidate_restart=yes` e `candidate_post_restart_command_relay=acknowledged`. Essa evidência cobre somente os fluxos exercitados em ambiente sintético; operações administrativas de credencial, comportamento de firmware físico, falha abrupta do processo antigo com sockets físicos e rede do servidor ainda não estão homologados.
