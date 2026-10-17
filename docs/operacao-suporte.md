# Playbook de suporte operacional

Este documento orienta a triagem de incidentes sem acesso direto ao banco de produção. Foi escrito com base nos endpoints existentes; ações que reenfileiram eventos ou enviam comandos à máquina exigem `PlatformAdmin` e registro no incidente. Não use este rascunho como autorização para reiniciar serviços, alterar pagamentos ou interromper uma máquina.

## Antes de agir

Registre no chamado:

- Data e hora com fuso (preferencialmente UTC e horário local), empresa, usuário que reportou e canal de retorno aprovado.
- Identificador e número de série da máquina, localização, versão do firmware se conhecida e impacto percebido pelo cliente.
- ID da sessão/transação/evento quando aplicável, intervalo afetado, mensagem de erro visível e ações já tentadas.
- Evidência sanitizada: não anexe tokens, senhas, CPF/CNPJ, payload MDB, cabeçalhos de autenticação ou dados de cartão.

Comece com consultas de leitura. Não desative monitoramento, revogue credenciais, marque pagamento manualmente, reenvie evento nem reinicie API/máquina sem autorização operacional e do cliente quando aplicável. Preserve a telemetria em funcionamento.

## Máquina sem comunicação

1. Confirme com o cliente se a máquina está energizada, conectada à rede e exibindo erro local. Registre quando começou e se houve intervenção física.
2. No painel, consulte `/machines` e `/telemetry`. Confira se o monitoramento está habilitado, o último contato, o estado MDB e se a máquina aparece conectada. A conexão em tempo real é mantida em memória pela instância; um processo reiniciado pode exigir reconexão do firmware.
3. Consulte os alertas ativos em `/alerts` e correlacione pelo ID/serial. A rotina de alertas roda aproximadamente a cada minuto: telemetria acima de 2 minutos gera aviso e acima de 5 minutos pode gerar alerta de máquina offline. `Roteamento da máquina indisponível` indica conexão recente sem owner conhecido; comandos/consultas podem falhar até a máquina reconectar a um nó compatível. Os limites do painel também distinguem online, atenção e offline; valide o instante do último contato em vez de inferir falha apenas por uma tela.
4. Se for `PlatformAdmin`, examine `GET /api/admin/worker-health` e compare o resumo `machineConnections` com `/machines` e `/telemetry`. `staleOnline` usa a janela de 90 segundos do roteamento distribuído; `onlineWithoutOwner` indica estado legado sem nó roteável. Compare também os heartbeats dos workers e a idade das filas, incluindo `telemetryCommandDispatches` e as idades de `Pending`/`Processing`. Um item `Processing` antigo indica resultado incerto: não repita o comando automaticamente; confira a sessão e o equipamento antes de qualquer nova ação. Esse estado é local ao processo e não substitui monitoramento externo. `GET /health/ready` verifica API/banco/migrations, não a conexão física de cada máquina.
5. Verifique `GET /api/admin/device-message-inbox?page=1&pageSize=50` apenas se houver indício de mensagem MDB falha. A tela correspondente é **Fila de Telemetria** em `/admin`; a consulta mostra contagens e metadados sem o payload MDB.
6. Não execute um comando MDB de diagnóstico como primeiro passo. `POST /api/telemetry/machines/{machineId}/mdb-status` envia uma solicitação ativa ao equipamento, é restrito a `Admin`/`PlatformAdmin` e pode expirar. Use somente com autorização, janela acordada com o cliente e registro do resultado.
7. Se a mensagem estiver em `Failed`, corrija/identifique a causa antes de solicitar reprocessamento. `POST /api/admin/device-message-inbox/{id}/retry` só aceita falha, recoloca o evento na fila e gera auditoria. Não apague linhas nem repita `Pending`/`Processing`.

Escalone quando várias máquinas da mesma rede/cliente perderem comunicação, quando os heartbeats dos workers estiverem antigos, quando a fila crescer sem reduzir ou quando a reconexão não ocorrer após uma manutenção aprovada. O procedimento atual não garante troca de container sem queda breve do WebSocket; releases exigem janela de reconexão conforme `deploy-seguro.md`.

## Pagamento pendente ou divergente

1. Registre o ID da transação, empresa, máquina, valor e moeda, horário e estado exibido. Não peça nem copie dados de cartão ou tokens.
2. Consulte a fila **Conciliação financeira** em `/admin` (ou `GET /api/payments/admin/reconciliation-queue?page=1&pageSize=50`, somente `PlatformAdmin`). Verifique `providerStatus`, idade da cobrança e última tentativa de conciliação.
3. Compare com o estado canônico da transação no Mercado Pago usando o acesso autorizado da operação. Uma notificação recebida é apenas um gatilho; o worker consulta o provedor. Considere que aprovações, recusas, estornos e contestações tardias podem alterar o estado posteriormente.
4. Aguarde o ciclo de conciliação quando a fila e o worker estiverem saudáveis. Se a idade exceder o limite operacional definido pela equipe ou o provedor mostrar estado incompatível, escale com os IDs e horários para o responsável financeiro.
5. Não altere manualmente o estado financeiro, não gere uma nova cobrança para “corrigir” a primeira sem avaliar duplicidade e não reenvie comando de venda à máquina. A repetição de um resultado aprovado não deve reenviar o mesmo comando.

O limite/SLA de atendimento financeiro ainda precisa ser acordado e preenchido na ficha de operação antes do lançamento comercial.

## Webhook de pagamento com falha

1. Verifique `GET /api/admin/worker-health` e a disponibilidade agregada das filas. Se a consulta de filas vier indisponível, investigue saúde/conectividade do banco pelo procedimento de infraestrutura; não reinicie a API apenas porque o worker do provedor falhou.
2. Consulte `GET /api/payments/admin/webhook-inbox?status=Failed&page=1&pageSize=50` (somente `PlatformAdmin`). Registre ID do evento, ID de pagamento, tipo/ação, tentativas e erro sanitizado. Não compartilhe segredos nem corpos integrais de webhook.
3. Consulte a fila de conciliação e confirme o estado canônico no provedor. Eventos repetidos são deduplicados; notificações podem chegar atrasadas e a inbox durável deve ser preservada.
4. Corrija primeiro a causa identificada (por exemplo, conectividade ou credencial, conforme procedimento autorizado). Só então, para um evento em falha final e após conferir o estado canônico, use a ação auditada **Reprocessar** ou `POST /api/payments/admin/webhook-inbox/{id}/retry`.
5. A rota aceita apenas `Failed`; redefine tentativas para zero, remove erro e dados de processamento e devolve o item a `Pending`. `Pending`, `Processing` ou `Retry` não devem ser reenfileirados manualmente. Acompanhe até `Processed` ou novo erro.

Não troque o segredo do webhook durante a triagem sem plano de rotação e validação das notificações; a configuração pode afetar todos os pagamentos da empresa/plataforma.

## Restauração de banco

**Estado: procedimento ainda não homologado.** Provedor, localização/retenção dos backups, comandos de restauração, RPO/RTO e pessoa autorizadora não estão registrados neste repositório. Não declare recuperação pronta nem execute restauração em produção com base apenas neste roteiro.

Quando ocorrer incidente de dados:

1. Abra incidente crítico e obtenha autorização do responsável de produção e do responsável pelos dados financeiros. Registre escopo, horário, RPO/RTO aprovado e impacto na telemetria.
2. Preserve o banco e os logs atuais conforme política de retenção; identifique backup e ponto no tempo no provedor. Não sobrescreva a única cópia recuperável.
3. Restaure primeiro em clone isolado, sem tráfego de máquinas, callbacks de pagamento ou workers externos. Use o procedimento específico do provedor, que precisa ser documentado pelo responsável de infraestrutura.
4. Valide versão e histórico `__EFMigrationsHistory`, integridade, empresas/usuários, transações, vendas/estornos, `DeviceMessageInbox` e `WebhookInboxMessages`; reconcilie contagens e amostras com evidências anteriores ao incidente. Não exponha payloads ou dados pessoais no relatório.
5. Obtenha aceite escrito dos responsáveis técnico, financeiro e de operação. Planeje o corte com backup do estado atual, janela comunicada e tratamento de reconexão das máquinas/webhooks. Preserve mensagens pendentes e compatibilidade de migrations.
6. Após eventual recuperação autorizada, observe readiness, workers/filas, conciliação financeira e reconexão dos dispositivos; registre discrepâncias e mantenha o incidente aberto até a reconciliação.

Antes de marcar este procedimento como pronto, preencher o provedor e referências internas autorizadas, retenção, RPO/RTO, responsáveis e comandos aprovados; executar e registrar uma restauração completa em clone recente.

## Comunicação com o cliente

Não há neste repositório canal de suporte, escala de plantão, contatos ou SLA aprovados. Antes do piloto, preencher e validar com a operação:

- Canal primário e alternativo, horário de atendimento e responsável de plantão: **pendente**.
- Responsável do cliente e contato alternativo autorizados: **registrar por cliente em sistema aprovado**.
- Critério para incidente crítico, prazo de primeira resposta e frequência de atualização: **pendente**.

Na atualização ao cliente, informe impacto conhecido, horário de início, ação em andamento, dependência do cliente e próximo horário de atualização. Não prometa prazo de recuperação sem evidência. Para incidentes financeiros, encaminhe apenas identificadores mínimos ao contato financeiro autorizado.

## Registro de encerramento

```text
Incidente / chamado:
Empresa / cliente:
Máquina(s) / serial(is):
Início (UTC e local):
Sintoma e impacto confirmado:
IDs de sessão, transação ou inbox (sem segredos):
Consultas e evidências:
Ações executadas / responsável / autorização:
Estado canônico do provedor, se aplicável:
Reconexão e filas verificadas:
Impacto financeiro ou de estoque pendente:
Comunicações realizadas e próxima atualização:
Causa raiz / acompanhamento:
Encerrado por / horário:
```
