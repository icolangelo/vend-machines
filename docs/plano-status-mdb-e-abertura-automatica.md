# Plano — status MDB e abertura automática de sessão

## Objetivo

Permitir que cada máquina tenha uma configuração de abertura automática de sessão e que seu estado MDB atual seja consultado, persistido e exibido. A abertura manual ou automática somente poderá ocorrer quando o estado confirmado for `enabled_state`.

Este plano reaproveita a sessão MDB, o acompanhamento de telemetria e o comando `ABRIR_SESSAO` já existentes.

## Resultado da revisão da implementação atual

A funcionalidade é viável, mas não deve ser implementada reutilizando diretamente todo o fluxo de comando existente. Foram identificados os seguintes pontos de risco:

### Críticos

1. **`MDB_STATUS` não possui ACK do dispositivo.** O método atual `SendCommandAsync` considera um comando concluído apenas quando recebe `type: "ack"`. Sem adaptação, cada consulta seria marcada como `ack_timeout`, permaneceria até 15 segundos com o canal de comandos da máquina ocupado e seria reenviada até três vezes, mesmo que a resposta de status já tivesse chegado.
2. **A resposta de status não possui `data.type`.** O `HandleDeviceMessageAsync` atual exige `data.command` e `data.type` antes de processar uma mensagem. A resposta informada possui `command` e `status`; portanto, hoje ela seria registrada/descartada sem atualizar o MDB.
3. **A verificação de sessão ativa não é suficiente contra concorrência.** Duas requisições simultâneas podem consultar o banco antes de qualquer uma inserir a sessão. É necessária uma restrição única parcial no banco para garantir apenas uma sessão com `ClosedAt IS NULL` por máquina.

### Altos

4. **Não se deve tratar a resposta como ACK genérico.** Isso mudaria o comportamento dos comandos que já funcionam. `MDB_STATUS` precisa de um caminho específico de requisição/resposta, deixando `SendCommandAsync` intacto para `ABRIR_SESSAO`, `FECHAR_SESSAO`, `VENDA_APROVADA` e demais comandos.
5. **A abertura automática não deve acontecer dentro do leitor do WebSocket.** A resposta deve primeiro concluir a requisição pendente, ser enfileirada e persistida. Só então outro fluxo tenta abrir a sessão. Isso evita bloquear a leitura do socket ou disputar o mesmo `CommandGate`.
6. **O timeout de seleção atual é de 2 minutos.** Uma sessão automática sem seleção será encerrada pelo fluxo atual. Como foi confirmado que ela deve reabrir, haverá ciclos controlados de abertura/fechamento enquanto ninguém usar a máquina. No primeiro rollout o timeout será mantido para não alterar a máquina de estados que já funciona.
7. **O estado MDB não deve dirigir o encerramento de uma sessão na primeira versão.** `inactive_state`, `disable_state`, status expirado ou desconhecido impedem uma nova abertura, mas não devem cancelar venda, pagamento ou entrega já iniciados. Os eventos atuais continuam sendo a fonte de verdade para o ciclo financeiro.

### Médios

8. **Polling simultâneo pode gerar pico de mensagens.** O worker deve aplicar jitter e processar as máquinas em lotes, em vez de consultar todas exatamente a cada múltiplo de 30 segundos.
9. **A migration atende PostgreSQL e SQLite por caminhos diferentes.** Além da migration do EF para produção, o projeto exige atualização de `EnsureSqliteCompatibilitySchema`. A migration gerada deve ser revisada para conter somente as novas colunas e índices, sem alterações de tipo em tabelas existentes.
10. **O cadastro usa a entidade `Machine` diretamente como contrato da API.** Ao acrescentar a configuração, o update deve copiar explicitamente apenas o novo campo e preservar os demais dados existentes. É recomendável introduzir DTOs para impedir que um payload parcial zere informações financeiras, estoque ou status.

## Estratégia de compatibilidade

Para proteger o comportamento que já está funcionando:

1. adicionar banco, coleta e visualização do status com `AutoOpenSessionEnabled = false` para todas as máquinas existentes;
2. executar inicialmente em modo observação, sem alterar a abertura manual;
3. validar `MDB_STATUS` com uma máquina piloto e medir respostas, tempo e duplicidades;
4. habilitar a validação MDB da abertura manual por configuração de backend;
5. habilitar a abertura automática somente nas máquinas selecionadas pelo usuário.

A abertura automática sempre exige `enabled_state`, mesmo durante o período em que a abertura manual ainda estiver em modo de compatibilidade.

## Glossário de estados MDB

| Estado recebido | Texto exibido | Significado | Pode abrir sessão? |
|---|---|---|---|
| `inactive_state` | Sem comunicação MDB | A placa está sem comunicação ou acabou de ser ligada. | Não |
| `disable_state` | MDB bloqueado | Há comunicação, mas a máquina ainda não autorizou o funcionamento ou existe algum bloqueio. | Não |
| `enabled_state` | Disponível para sessão | Sistema ativo e sem sessão aberta. | Sim |
| `idle_state` | Aguardando seleção | Existe uma sessão aberta e o usuário ainda não selecionou um produto. | Não |
| `vend_state` | Venda em andamento | O produto foi selecionado e a máquina aguarda venda aprovada ou negada. | Não |
| desconhecido/ausente | Status MDB desconhecido | Ainda não houve resposta válida ou a informação expirou. | Não |

O estado MDB é diferente do estado de conectividade WebSocket (`online/offline`) e do estado interno da sessão (`Opening`, `AwaitingSelection` etc.). Os três devem continuar separados.

## Experiência no cadastro da máquina

Na aba **Vending** do cadastro/edição:

- adicionar a opção **Abrir sessão automaticamente**;
- incluir uma descrição: “Quando o MDB estiver disponível, o sistema abrirá uma sessão sem ação do operador”;
- ao ativar a abertura automática, garantir também a ativação do acompanhamento de telemetria na mesma operação;
- ao desativar a abertura automática, manter o acompanhamento como está;
- salvar a configuração na própria máquina;
- na edição, mostrar o status MDB atual, sua descrição e o horário da última atualização;
- incluir o botão **Consultar status MDB**;
- incluir o botão **Abrir sessão**, habilitado apenas quando:
  - a máquina estiver conectada;
  - o acompanhamento estiver ativo;
  - o status MDB atual e não expirado for `enabled_state`;
  - não existir sessão física aberta nem tentativa de abertura em andamento.

Ao clicar em **Abrir sessão**, o backend deve validar novamente as condições. A regra não pode depender apenas do bloqueio visual no frontend.

## Fluxo de consulta do status

1. O backend envia `MDB_STATUS` para a máquina.
2. O dispositivo responde com uma mensagem equivalente a:

   ```json
   {
     "id": 13,
     "target": "1020304050-2",
     "type": "msg",
     "data": {
       "command": "status",
       "status": "enabled_state"
     }
   }
   ```

3. O backend valida `data.command == "status"` e normaliza `data.status`.
4. A mensagem `type: "msg"` é a própria resposta da consulta; o dispositivo não envia um ACK separado para `MDB_STATUS`.
5. O backend continua enviando o ACK da mensagem recebida, como já faz para qualquer `type: "msg"`.
6. O último estado válido e o horário da resposta são persistidos.
7. O backend publica a atualização para o painel em tempo real.
8. Se a resposta for `enabled_state` e a abertura automática estiver ativa, o backend agenda uma tentativa de abertura idempotente fora do leitor do WebSocket.

Valores não reconhecidos devem ser armazenados para diagnóstico, mas tratados funcionalmente como “Status MDB desconhecido”.

### Tratamento específico de requisição/resposta

- criar `SendStatusRequestAsync` separado do `SendCommandAsync` atual;
- enviar `MDB_STATUS` uma única vez por ciclo;
- manter no máximo uma consulta de status pendente por conexão;
- considerar a consulta respondida ao receber `type: "msg"`, o mesmo `id` da requisição, `data.command: "status"` e um dos estados conhecidos;
- não exigir `type: "ack"` vindo do dispositivo;
- aguardar no máximo 5 segundos pela resposta; se não chegar, encerrar a tentativa e aguardar o próximo ciclo de 30 segundos, sem três retransmissões imediatas;
- completar a requisição pendente antes de encaminhar a mensagem ao processamento de domínio;
- persistir o comando como `responded`, `response_timeout` ou `failed`, e não como `acknowledged`;
- aceitar e persistir respostas espontâneas de `command: "status"` mesmo sem requisição pendente.

Como existe somente uma consulta pendente por máquina, `command=status` identifica o tipo de resposta e o `id` confirma a correlação com a requisição atual. Respostas espontâneas continuam atualizando o estado, mas não concluem uma requisição com outro `id`.

## Periodicidade proposta

- consultar imediatamente após a conexão/reconexão do dispositivo;
- consultar ao abrir a tela de edição, pelo botão manual;
- consultar periodicamente enquanto a máquina estiver conectada e com acompanhamento ativo;
- intervalo inicial sugerido: **30 segundos**, configurável no backend;
- considerar o status expirado após **90 segundos** sem resposta válida;
- aplicar jitter de até 5 segundos e processar em lotes para distribuir a carga;
- aplicar timeout e registrar falha quando a resposta de `MDB_STATUS` não chegar;
- limitar a uma consulta pendente por máquina.

Uma resposta espontânea de status também deve atualizar o estado, mesmo que não tenha sido causada pelo ciclo periódico.

## Regra da abertura automática

A configuração `AutoOpenSessionEnabled` expressa intenção; ela não cria uma sessão sozinha. O orquestrador abre a sessão somente se todas as condições forem verdadeiras:

1. máquina pertence à empresa autenticada;
2. máquina conectada e acompanhamento ativo;
3. último status MDB é `enabled_state` e ainda está válido;
4. não existe `MachineSession` com `ClosedAt == null`;
5. não existe abertura em andamento para a mesma máquina;
6. o status utilizado ainda é o mais recente;
7. para uma reabertura, `MdbStatusUpdatedAt` é posterior ao `ClosedAt` da última sessão;
8. a última tentativa não está dentro do período de cooldown.

Sessões automáticas devem usar `Source = "automatic_mdb"`. Sessões abertas pelo usuário permanecem com `Source = "panel"`.

Se `ABRIR_SESSAO` falhar, registrar o evento e aguardar uma nova consulta após um cooldown sugerido de 30 segundos. Não repetir imediatamente. Se outra instância do backend já tiver criado uma sessão, a segunda tentativa deve terminar sem enviar outro comando.

Após o encerramento físico da sessão, a automação não deve reutilizar um `enabled_state` antigo. Ela deve solicitar ou receber um novo status confirmado antes de abrir a próxima sessão. Isso evita um ciclo de reabertura indevido.

O timeout atual de 2 minutos para seleção será preservado. Se ele encerrar uma sessão automática, o sistema somente abrirá outra após receber um novo `enabled_state`. Esse comportamento deve ser medido; remover ou ampliar o timeout de sessões automáticas será uma alteração posterior e separada.

Status MDB diferente de `enabled_state`, expirado ou ausente nunca deve, isoladamente, fechar uma sessão ativa, negar uma venda, cancelar um Pix ou solicitar estorno.

## Alterações no backend

### Dados

Adicionar em `Machine`:

- `AutoOpenSessionEnabled: bool`, padrão `false`.

Adicionar em `MachineConnectionState`:

- `MdbStatus: string?`;
- `MdbStatusRaw: string?`, opcional para diagnóstico;
- `MdbStatusUpdatedAt: DateTime?`;
- `MdbStatusRequestAt: DateTime?`;
- `MdbStatusRequestMessageId: int?`;
- `AutoOpenLastAttemptAt: DateTime?`;
- `AutoOpenLastError: string?`.

Criar migration e atualizar a inicialização compatível com SQLite usada pelo projeto. Antes de criar o índice de sessão única:

- executar uma verificação de sessões duplicadas com `ClosedAt IS NULL`;
- não fechar sessões automaticamente durante a migration;
- interromper o deploy e corrigir os registros se houver duplicidade;
- gerar backup do banco;
- revisar a migration para garantir que ela não altere tipos de colunas existentes.

### Serviços

- criar um `MdbStatusPollingWorker` para selecionar máquinas elegíveis e disparar `MDB_STATUS`;
- adicionar `SendStatusRequestAsync` sem alterar a semântica do `SendCommandAsync` atual;
- permitir o rastreamento de comandos sem sessão ativa, pois a consulta de status não deve criar uma `MachineSession`;
- processar `command=status` antes da validação que exige `data.type`;
- criar um serviço/regra `TryAutoOpenSessionAsync`, reutilizando `SessionOrchestrator.StartSessionAsync`;
- agendar `TryAutoOpenSessionAsync` depois que a resposta for persistida, fora do leitor do WebSocket;
- validar o MDB dentro de `StartSessionAsync`; durante o rollout, a abertura manual pode respeitar a configuração temporária de compatibilidade, mas a automática nunca pode ignorar o MDB;
- manter um lock por máquina no processo e uma garantia no banco para somente uma sessão com `ClosedAt == null`;
- tratar a violação da restrição única como `session_already_active`, sem reenviar `ABRIR_SESSAO`;
- registrar consultas manuais, mudanças de estado, tentativas automáticas e falhas relevantes nos eventos de telemetria;
- não criar um registro de comando para cada polling bem-sucedido e repetido; persistir o último estado e manter o polling normal apenas em métricas/log técnico para evitar crescimento excessivo do banco;
- publicar `mdb.status.updated` e mudanças de sessão no canal já usado pelo painel.

### API

Manter URLs em inglês e textos em português:

- `GET /api/machines/{id}`: incluir configuração e resumo MDB;
- `POST/PUT /api/machines`: aceitar `autoOpenSessionEnabled`;
- `POST /api/telemetry/machines/{machineId}/mdb-status`: solicitar atualização e retornar `202 Accepted`;
- `GET /api/telemetry/connections`: incluir `mdbStatus`, `mdbStatusUpdatedAt`, `mdbStatusFresh` e `autoOpenSessionEnabled`;
- `POST /api/telemetry/machines/{machineId}/sessions`: retornar conflito se MDB não estiver em `enabled_state` ou estiver expirado.

Ativar `autoOpenSessionEnabled` e `MonitoringEnabled` deve ser uma única operação no backend. O frontend não deve executar duas requisições independentes que possam deixar a máquina parcialmente configurada.

Códigos de conflito sugeridos:

- `mdb_status_unavailable`;
- `mdb_status_stale`;
- `mdb_not_ready`;
- `session_already_active`;
- `session_opening_in_progress`;
- `machine_offline`;
- `monitoring_inactive`.

## Alterações no frontend

- ampliar o tipo `Machine` com `autoOpenSessionEnabled`;
- ampliar `TelemetryConnection` com os campos MDB;
- adicionar os controles à aba **Vending** de `MachineFormPage`;
- exibir badge, descrição e “Atualizado há…”;
- implementar ação de consulta e atualizar a tela por evento em tempo real ou polling curto;
- chamar a abertura manual existente somente quando o resumo MDB permitir;
- mostrar mensagens específicas vindas dos códigos de conflito;
- manter todos os textos em português e nomes de componentes/tipos em inglês.

## Observabilidade e segurança

- toda abertura deve registrar origem, usuário quando houver, status MDB utilizado e horário;
- mudanças de configuração devem registrar usuário, valor anterior e novo valor;
- não confiar no `target` enviado pelo dispositivo para localizar a máquina; usar a conexão autenticada;
- rejeitar status vindo de conexão que não corresponda à máquina/empresa;
- não registrar segredos ou payloads sensíveis;
- métricas recomendadas: consultas enviadas, respostas válidas, timeouts, aberturas automáticas, bloqueios idempotentes e falhas por máquina.
- o status MDB deve ser usado como autorização para abrir, não como fonte para alterar retroativamente estados financeiros.

## Critérios de aceite

1. É possível ativar e desativar a abertura automática no cadastro da máquina.
2. A configuração permanece salva após recarregar a página.
3. O sistema consulta `MDB_STATUS` após conexão e periodicamente.
4. A resposta `type=msg`, `command=status` conclui a consulta sem exigir ACK do dispositivo.
5. A ausência de resposta não provoca três reenvios nem bloqueia outros comandos durante 15 segundos.
6. Cada resposta válida atualiza o estado e o horário persistidos.
7. A interface apresenta os cinco estados com as descrições definidas.
8. O botão manual só fica disponível em `enabled_state` atual e o backend repete essa validação após o rollout de compatibilidade.
9. Em `inactive_state`, `disable_state`, `idle_state`, `vend_state`, status desconhecido ou expirado, nenhuma nova sessão automática é aberta.
10. Com automação ativa, a chegada de um novo `enabled_state` abre uma sessão sem ação do usuário.
11. Duas respostas simultâneas ou duas instâncias do backend não abrem duas sessões.
12. Uma falha de comando não causa repetição contínua; há cooldown e evento de erro.
13. Após uma sessão ser encerrada, é necessária uma nova confirmação de `enabled_state` para reabrir.
14. Máquina desconectada mantém o último status para histórico, mas ele é exibido como expirado e não autoriza abertura.
15. Status MDB desfavorável ou expirado não interrompe sessão, pagamento ou entrega em andamento.
16. Aberturas manuais aparecem com origem `panel`; automáticas, com `automatic_mdb`.

## Testes previstos

### Backend

- parsing dos cinco estados e de valor desconhecido;
- resposta de status sem ACK separado;
- resposta sem `data.type`;
- consulta respondida não é marcada como timeout nem reenviada;
- consulta sem resposta libera o canal e aguarda o próximo ciclo;
- persistência e expiração do último status;
- abertura manual permitida e bloqueada por cada estado;
- abertura automática ao receber `enabled_state`;
- automação desligada não abre sessão;
- idempotência com respostas duplicadas e concorrência;
- status antigo não reabre sessão após fechamento;
- timeout, desconexão e falha do comando;
- status desfavorável durante pagamento não cancela a sessão;
- criação do índice com e sem sessões ativas preexistentes;
- migration PostgreSQL e compatibilidade SQLite sem alteração de tipos existentes;
- isolamento por empresa;
- integração do worker com conexão/reconexão.

### Frontend

- carregar e salvar a nova opção;
- renderizar cada descrição;
- habilitar/desabilitar botões conforme conectividade, status e sessão;
- apresentar erros específicos da API;
- atualizar status sem recarregar a página.

### Simulador

- aceitar `MDB_STATUS`;
- permitir escolher cada estado MDB;
- responder no mesmo formato do dispositivo real;
- oferecer cenários de duplicidade, atraso, ausência de resposta e transição `enabled → idle → vend → enabled`.

## Ordem sugerida de implementação

1. Preflight do banco, DTOs, modelo, migration e garantia de sessão única.
2. Caminho específico de requisição/resposta sem ACK e parsing de `command=status`.
3. Consulta manual, persistência, eventos e exposição do estado pela API.
4. Atualização do simulador e validação com uma máquina real em modo observação.
5. Worker periódico com jitter, expiração e métricas.
6. Validação MDB na abertura manual, inicialmente protegida por configuração de rollout.
7. Automação idempotente com cooldown e exigência de status posterior ao último fechamento.
8. Cadastro e visualização no frontend.
9. Piloto por máquina e expansão gradual.

## Decisões confirmadas

1. Polling a cada 30 segundos e expiração após 90 segundos.
2. Quando a configuração automática estiver ativa, a máquina deve reabrir outra sessão após o encerramento e uma nova confirmação de `enabled_state`.
3. `MDB_STATUS` não recebe ACK separado do dispositivo.
4. A resposta é `type: "msg"` com `data.command: "status"` e `data.status` contendo o estado MDB.

## Recomendação adotada para o acompanhamento

Ao ativar **Abrir sessão automaticamente**, o backend também deve ativar o acompanhamento de telemetria na mesma transação e informar isso na interface. Desativar a abertura automática não desativa o acompanhamento. Essa regra evita uma configuração “ativa” que nunca executa, sem desligar recursos que possam continuar sendo usados pelo operador.
